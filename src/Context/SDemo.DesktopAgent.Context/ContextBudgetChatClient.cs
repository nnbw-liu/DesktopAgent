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
    int MaxOutputTokens = 0,
    // P2c：超长工具结果的落盘 seam。为 null（没有工作区 / 用户关掉开关）时退回纯截断。
    IToolResultSpillStore? Spill = null);

/// <summary>
/// 工具循环内的拦截器（§3.7 分支 A，spike 结论见 p1-report.md）：
/// MAF 1.25 的 <c>FunctionInvokingChatClient</c> 插在这个装饰器<b>外层</b>，
/// 所以每次 LLM 请求（含工具结果回灌）都会先经过这里。
/// 只做确定性收缩：超长工具结果<b>落盘后只留头尾预览</b>（P2c）+ 从最旧整块淘汰历史，
/// 绝不拆散 tool_call / tool_result，也不在循环中途触发摘要。
/// </summary>
public sealed class ContextBudgetChatClient : DelegatingChatClient
{
    private readonly ContextBudgetChatOptions _options;
    private readonly ContextRunTelemetry _telemetry;

    /// <summary>已经记过账的 CallId：同一条结果会在后续每个请求里再走一次落盘，不能重复计数。</summary>
    private readonly HashSet<string> _spilledCalls = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 读文件的输出不落盘：它本来就能按 offset 再读一次，落盘等于把内容复制一份，
    /// 还会让模型在「读日志」和「读文件」之间绕圈（DSH 同样显式跳过 <c>read</c>）。
    /// </summary>
    private static readonly IReadOnlySet<string> NoSpillTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_file" };

    private IReadOnlyList<ChatMessage> Prepare(List<ChatMessage> messages)
    {
        _telemetry.RequestCount++;

        if (!_options.Enabled || messages.Count == 0)
        {
            _telemetry.EstimatedInputTokens += Estimate(messages);
            return messages;
        }

        var changed = false;

        // 1. 单条工具结果收缩：这是工具循环爆窗最常见的原因。
        //    P2c：顺带把超长结果落盘，上下文里换成「头尾预览 + read_file 回读指引」。
        if (_options.ToolResultChars > 0)
        {
            var toolNames = _options.Spill is null ? null : MapToolNames(messages);

            for (var index = 0; index < messages.Count; index++)
            {
                if (TryShrinkToolResults(messages[index], _options.ToolResultChars, toolNames, out var replaced))
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

    /// <summary>从历史里的 <c>tool_calls</c> 建 CallId → 工具名映射（<see cref="FunctionResultContent"/> 不带名字）。</summary>
    private static Dictionary<string, string> MapToolNames(IReadOnlyList<ChatMessage> messages)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { CallId.Length: > 0, Name.Length: > 0 } call)
                {
                    map[call.CallId] = call.Name;
                }
            }
        }

        return map;
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

    private bool TryShrinkToolResults(
        ChatMessage message,
        int maxChars,
        IReadOnlyDictionary<string, string>? toolNames,
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

                var name = result.CallId is { Length: > 0 } callId && toolNames is not null
                    ? toolNames.GetValueOrDefault(callId)
                    : null;

                var spill = TrySpill(result.CallId, name, text);

                contents.Add(new FunctionResultContent(
                    result.CallId,
                    spill is null
                        ? Truncate(text, maxChars, spillAttempted: _options.Spill is not null)
                        : SpilledPreview(text, spill, maxChars)));
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

    /// <summary>尽力而为地落盘：没有 seam、是读文件的结果、或者写盘失败都返回 null。</summary>
    private ToolResultSpill? TrySpill(string? callId, string? toolName, string text)
    {
        if (_options.Spill is null || (toolName is not null && NoSpillTools.Contains(toolName)))
        {
            return null;
        }

        var spill = _options.Spill.Save(callId ?? string.Empty, text);

        // 同一条结果会在后续每个请求里再走一次这里；按 callId 去重，报告里数的是「条」而不是「次」。
        var counted = string.IsNullOrEmpty(callId) || _spilledCalls.Add(callId);

        if (counted)
        {
            _telemetry.RecordSpill(spill is not null);
        }

        return spill;
    }

    /// <summary>
    /// 落盘后的替代文本：头尾预览 + 全文位置 + 回读指引。
    /// 头部在**行边界**收口，回读的 <c>offset</c> 才能从下一行正好接上。
    /// </summary>
    private static string SpilledPreview(string text, ToolResultSpill spill, int maxChars)
    {
        var headLimit = Math.Min(HeadChars, maxChars);

        if (headLimit > text.Length)
        {
            headLimit = text.Length;
        }

        var newline = text.LastIndexOf('\n', Math.Max(0, headLimit - 1), headLimit);

        // 找到的换行太靠前（例如输出开头就是空行）时不要它：否则预览会缩成几个字符。
        var head = newline >= headLimit / 2 ? newline + 1 : headLimit;
        var headLines = CountNewlines(text.AsSpan(0, head));

        var tailLimit = Math.Min(TailChars, Math.Max(0, maxChars - head));
        var tailStart = Math.Max(head, text.Length - tailLimit);
        var removed = tailStart - head;

        var builder = new StringBuilder(head + tailLimit + 320);
        builder.Append("[输出已截断] 全文 ").Append(text.Length)
            .Append(" 字符，已保存到 ").Append(spill.RelativePath).Append('\n');
        builder.Append("前 ").Append(head).Append(" 字符：\n");
        builder.Append(text, 0, head);
        builder.Append("\n…（中间省略 ").Append(removed).Append(" 字符）…\n");

        if (tailStart < text.Length)
        {
            builder.Append("后 ").Append(text.Length - tailStart).Append(" 字符：\n");
            builder.Append(text, tailStart, text.Length - tailStart).Append('\n');
        }

        builder.Append("需要更多内容时用 read_file(\"").Append(spill.RelativePath).Append("\", offset: ");

        if (headLines > 0)
        {
            builder.Append(headLines + 1).Append(") 分页回读；不要重跑同一条命令。");
        }
        else
        {
            // 整份输出没有换行：按行号翻不动，只能提示调大单行上限一次多看一些。
            builder.Append("1, maxLineChars: 20000) 回读；不要重跑同一条命令。");
        }

        return builder.ToString();
    }

    private static int CountNewlines(ReadOnlySpan<char> text)
    {
        var count = 0;

        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string Truncate(string text, int maxChars, bool spillAttempted)
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
        builder.Append("\n…（工具结果过长，已省略 ").Append(removed).Append(" 字符");

        if (spillAttempted)
        {
            builder.Append("；全文未能落盘");
        }

        builder.Append("）\n");

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
