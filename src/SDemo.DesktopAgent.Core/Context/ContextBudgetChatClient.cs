using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Context;

/// <summary>中途裁剪参数。预算与 <see cref="ContextBuilder"/> 一致，不再单独计算。</summary>
public sealed record ContextBudgetChatOptions(
    bool Enabled,
    ContextBudget Budget,
    ITokenCounter Counter,
    double CalibrationFactor,
    int KeepRecentTurns,
    int ToolResultChars,
    int MaxOutputTokens = 0);

/// <summary>
/// 工具循环内的拦截器（§3.7 分支 A，spike 结论见 p1-report.md）：
/// MAF 1.25 的 <c>FunctionInvokingChatClient</c> 插在这个装饰器<b>外层</b>，
/// 所以每次 LLM 请求（含工具结果回灌）都会先经过这里。
/// 只做确定性收缩：强裁单条工具结果 + 从最旧整块淘汰历史，绝不拆散 tool_call / tool_result。
/// </summary>
public sealed class ContextBudgetChatClient : DelegatingChatClient
{
    private readonly ContextBudgetChatOptions _options;
    private readonly ContextRunTelemetry _telemetry;

    public ContextBudgetChatClient(
        IChatClient innerClient,
        ContextBudgetChatOptions options,
        ContextRunTelemetry telemetry)
        : base(innerClient)
    {
        _options = options;
        _telemetry = telemetry;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(messages.ToList());

        await foreach (var update in base.GetStreamingResponseAsync(prepared, WithOutputLimit(options), cancellationToken))
        {
            yield return update;
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(messages.ToList());
        return await base.GetResponseAsync(prepared, WithOutputLimit(options), cancellationToken).ConfigureAwait(false);
    }

    private ChatOptions? WithOutputLimit(ChatOptions? options)
    {
        if (_options.MaxOutputTokens <= 0)
        {
            return options;
        }

        var result = options ?? new ChatOptions();
        result.MaxOutputTokens = _options.MaxOutputTokens;
        return result;
    }

    /// <summary>工具结果的头部 / 尾部各保留多少字符；中间用省略标记。</summary>
    private const int HeadChars = 1200;
    private const int TailChars = 300;

    private IReadOnlyList<ChatMessage> Prepare(List<ChatMessage> messages)
    {
        _telemetry.RequestCount++;

        if (!_options.Enabled || messages.Count == 0)
        {
            _telemetry.EstimatedInputTokens += Estimate(messages);
            return messages;
        }

        var changed = false;

        // 1. 单条工具结果强裁：这是工具循环爆窗最常见的原因。
        if (_options.ToolResultChars > 0)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                if (TryTruncateToolResults(messages[index], _options.ToolResultChars, out var replaced))
                {
                    messages[index] = replaced;
                    changed = true;
                }
            }
        }

        // 2. 仍超预算：从最旧整块淘汰（块 = 一条 user 消息及其后的 assistant / tool 消息）。
        var rawBudget = EffectiveRawBudget();
        if (Estimate(messages) > rawBudget)
        {
            changed |= EvictOldBlocks(messages, rawBudget);
        }

        if (changed)
        {
            _telemetry.RecordMidTurnTrim();
        }

        _telemetry.EstimatedInputTokens += Estimate(messages);
        return messages;
    }

    private int EffectiveRawBudget()
    {
        var factor = _options.CalibrationFactor <= 0 ? 1.0 : _options.CalibrationFactor;
        return (int)Math.Floor(Math.Max(0, _options.Budget.Total) / factor);
    }

    /// <summary>
    /// 从最旧块开始整块淘汰，永远保留：
    /// system 前缀、最后一块（当前用户输入 + 它在循环里产生的 tool_call / tool_result）。
    /// </summary>
    private bool EvictOldBlocks(List<ChatMessage> messages, int rawBudget)
    {
        var systemCount = messages.TakeWhile(message => message.Role == ChatRole.System).Count();
        var blocks = BuildBlocks(messages, systemCount);

        if (blocks.Count <= 1)
        {
            return false;
        }

        var evicted = false;

        while (blocks.Count > 1 && Estimate(messages) > rawBudget)
        {
            // 保留最后一块（当前轮），从倒数第二块开始往前淘汰。
            var block = blocks[blocks.Count - 2];
            messages.RemoveRange(block.Start, block.End - block.Start);
            blocks.RemoveAt(blocks.Count - 2);
            ShiftBlocks(blocks, block.Start, block.End - block.Start);
            evicted = true;
        }

        // 兜底：如果淘汰后第一条非 system 消息是 tool 结果，它已经失去对应的 tool_call，一起丢掉。
        while (systemCount < messages.Count && messages[systemCount].Role == ChatRole.Tool)
        {
            messages.RemoveAt(systemCount);
            evicted = true;
        }

        return evicted;
    }

    private static List<MessageBlock> BuildBlocks(IReadOnlyList<ChatMessage> messages, int systemCount)
    {
        var blocks = new List<MessageBlock>();
        var start = systemCount;

        for (var index = systemCount; index < messages.Count; index++)
        {
            if (index > start && messages[index].Role == ChatRole.User)
            {
                blocks.Add(new MessageBlock(start, index));
                start = index;
            }
        }

        if (start < messages.Count)
        {
            blocks.Add(new MessageBlock(start, messages.Count));
        }

        return blocks;
    }

    private static void ShiftBlocks(List<MessageBlock> blocks, int removedStart, int removedCount)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].Start <= removedStart)
            {
                continue;
            }

            blocks[index] = blocks[index] with
            {
                Start = blocks[index].Start - removedCount,
                End = blocks[index].End - removedCount
            };
        }
    }

    private static bool TryTruncateToolResults(
        ChatMessage message,
        int maxChars,
        out ChatMessage replaced)
    {
        replaced = message;
        var oversized = false;

        var contents = new List<AIContent>(message.Contents.Count);

        foreach (var content in message.Contents)
        {
            if (content is FunctionResultContent result && result.Result?.ToString() is { Length: > 0 } text
                && text.Length > maxChars)
            {
                oversized = true;
                contents.Add(new FunctionResultContent(
                    result.CallId,
                    Truncate(text, maxChars)));
                continue;
            }

            contents.Add(content);
        }

        if (!oversized)
        {
            return false;
        }

        replaced = new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            AdditionalProperties = message.AdditionalProperties
        };

        return true;
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var head = Math.Min(HeadChars, maxChars);
        var tail = Math.Min(TailChars, Math.Max(0, maxChars - head));
        var removed = text.Length - head - tail;

        var builder = new StringBuilder(head + tail + 64);
        builder.Append(text.AsSpan(0, head));
        builder.Append("\n…（工具结果过长，已省略 ").Append(removed).Append(" 字符）\n");

        if (tail > 0)
        {
            builder.Append(text.AsSpan(text.Length - tail));
        }

        return builder.ToString();
    }

    /// <summary>估算一条 AI 消息的 token：正文 + 工具调用参数 + 工具结果。</summary>
    private int Estimate(IReadOnlyList<ChatMessage> messages)
    {
        var total = 0;

        foreach (var message in messages)
        {
            var text = new StringBuilder();

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } chunk:
                        text.Append(chunk.Text);
                        break;
                    case FunctionResultContent result when result.Result?.ToString() is { Length: > 0 } value:
                        text.Append(value);
                        break;
                    case FunctionCallContent call:
                        text.Append(call.Name).Append('(')
                            .Append(JsonSerializer.Serialize(call.Arguments))
                            .Append(')');
                        break;
                }
            }

            total += _options.Counter.CountMessage(message.Role.ToString(), text.ToString());
        }

        return total;
    }

    private sealed record MessageBlock(int Start, int End);
}
