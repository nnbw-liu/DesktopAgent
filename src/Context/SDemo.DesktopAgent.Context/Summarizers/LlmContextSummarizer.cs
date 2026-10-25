using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 模型摘要（P2 §3.2 + P2b §3.3.2）：固定模板 + 超时 + 格式校验。
/// 任何失败都返回 <see cref="SummaryResult.Failed"/>，由 <see cref="ContextCompactor"/> 决定是否兜底，
/// 不会把异常抛给聊天主流程。
/// <para>
/// P2b：请求形状改成「原对话真前缀 + 末尾压缩指令」——system 用会话自己的提示词、
/// 被遮蔽区域逐条回放、工具声明原样带上，让摘要调用能吃到上一次请求的 KV 缓存。
/// 带上工具后个别模型会直接发起工具调用（没有文本），这时**去掉工具重试一次**，
/// 而不是把这次压缩降级成确定性兜底。
/// </para>
/// <para>
/// R1.4：输入 token 按实际发出的 system + 各消息 + 工具声明估算（与对话侧同一个估算器）；
/// 输出 token 按最终保留的摘要正文估算。重试时两次请求都计入成本。
/// </para>
/// </summary>
public sealed class LlmContextSummarizer : IContextSummarizer
{
    private readonly ISummaryChatTransport _transport;
    private readonly ITokenCounter _counter;

    public LlmContextSummarizer(ISummaryChatTransport transport, ITokenCounter counter)
    {
        _transport = transport;
        _counter = counter;
    }

    public async Task<SummaryResult> SummarizeAsync(
        SummaryRequest request,
        CancellationToken cancellationToken)
    {
        var messages = SummaryPrompt.BuildMessages(request);
        var inputTokens = CountMessages(messages) + request.ToolSchemaTokens(_counter);
        var outputTokens = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        string text;

        try
        {
            text = await CompleteAsync(request, messages, timeout.Token).ConfigureAwait(false);

            // 声明了工具却没有文本：模型大概率去调用工具了。去掉工具再试一次，
            // 前缀仍然一致（工具段本来就在 system 之后、消息之前，去掉它不影响消息部分）。
            if (string.IsNullOrWhiteSpace(text) && request.Tools is { Count: > 0 })
            {
                text = await CompleteAsync(request with { Tools = null }, messages, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("摘要请求超时", inputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 网络 / 鉴权 / 服务端 5xx：都按「摘要不可用」处理，走兜底。
            return Failed(ex.Message, inputTokens);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Failed("摘要模型没有返回文本", inputTokens);
        }

        var capped = Normalize(text, request);
        outputTokens += _counter.Count(capped);
        var validation = SummaryFormat.Validate(capped, request.MaxTokens, _counter, requireSources: true);

        // 格式不符时重试一次（模板输出是硬要求，模型偶尔会漏小节）；
        // 超时 / 网络错误不重试，直接让调用方走确定性兜底。
        if (!validation.Ok && !timeout.IsCancellationRequested)
        {
            var repairMessages = SummaryPrompt.BuildMessages(request, SummaryPrompt.BuildRepairHint(validation.Reason));
            inputTokens += CountMessages(repairMessages);
            var withoutTools = request with { Tools = null };

            try
            {
                text = await CompleteAsync(withoutTools, repairMessages, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed("摘要请求超时（格式重试）", inputTokens, outputTokens);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(ex.Message, inputTokens, outputTokens);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return Failed("摘要模型没有返回文本（格式重试）", inputTokens, outputTokens);
            }

            capped = Normalize(text, request);
            outputTokens += _counter.Count(capped);
            validation = SummaryFormat.Validate(capped, request.MaxTokens, _counter, requireSources: true);
        }

        if (!validation.Ok)
        {
            return new SummaryResult(
                capped,
                UsedModel: false,
                Tokens: _counter.Count(capped),
                Error: validation.Reason,
                InputTokens: inputTokens,
                OutputTokens: outputTokens);
        }

        return new SummaryResult(
            capped,
            UsedModel: true,
            _counter.Count(capped),
            Error: null,
            InputTokens: inputTokens,
            OutputTokens: outputTokens);
    }

    private Task<string> CompleteAsync(
        SummaryRequest request,
        IReadOnlyList<AIChatMessage> messages,
        CancellationToken cancellationToken) =>
        _transport.CompleteAsync(
            new SummaryChatRequest(request.Provider, request.ModelId, messages, request.Tools, request.MaxTokens),
            cancellationToken);

    /// <summary>截断到上限 + 把「第几条消息」的来源序号换回会话里的绝对序号。</summary>
    private string Normalize(string text, SummaryRequest request) =>
        SummaryFormat.RemapSources(
            SummaryFormat.Cap(text, request.MaxTokens, _counter),
            request.ChunkStartOrder,
            request.Chunk.Count);

    /// <summary>一次摘要请求的输入成本：每条消息按对话侧同一个估算器计价（含消息开销）。</summary>
    private int CountMessages(IReadOnlyList<AIChatMessage> messages)
    {
        var total = 0;

        foreach (var message in messages)
        {
            total += _counter.CountMessage(message.Role.Value, message.Text);
        }

        return total;
    }

    private static SummaryResult Failed(string error, int inputTokens, int outputTokens = 0) =>
        new(string.Empty, false, 0, error, inputTokens, outputTokens);
}
