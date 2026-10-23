namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 模型摘要（P2 §3.2）：固定模板 + 20s 超时 + 格式校验。
/// 任何失败都返回 <see cref="SummaryResult.Failed"/>，由 <see cref="ContextCompactor"/> 决定是否兜底，
/// 不会把异常抛给聊天主流程。
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
        var userPrompt = SummaryPrompt.BuildUserPrompt(
            request.PreviousSummary,
            request.Chunk,
            request.ChunkStartOrder,
            request.InputMaxTokens);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        string text;

        try
        {
            text = await _transport.CompleteAsync(
                request.Provider,
                request.ModelId,
                SummaryPrompt.System,
                userPrompt,
                request.MaxTokens,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SummaryResult.Failed("摘要请求超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 网络 / 鉴权 / 服务端 5xx：都按「摘要不可用」处理，走兜底。
            return SummaryResult.Failed(ex.Message);
        }

        var capped = SummaryFormat.Cap(text, request.MaxTokens, _counter);
        var validation = SummaryFormat.Validate(capped, request.MaxTokens, _counter, requireSources: true);

        // 格式不符时重试一次（模板输出是硬要求，模型偶尔会漏小节）；
        // 超时 / 网络错误不重试，直接让调用方走确定性兜底。
        if (!validation.Ok && !timeout.IsCancellationRequested)
        {
            try
            {
                text = await _transport.CompleteAsync(
                    request.Provider,
                    request.ModelId,
                    SummaryPrompt.System,
                    userPrompt + SummaryPrompt.BuildRepairHint(validation.Reason),
                    request.MaxTokens,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SummaryResult.Failed("摘要请求超时（格式重试）");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return SummaryResult.Failed(ex.Message);
            }

            capped = SummaryFormat.Cap(text, request.MaxTokens, _counter);
            validation = SummaryFormat.Validate(capped, request.MaxTokens, _counter, requireSources: true);
        }

        if (!validation.Ok)
        {
            return new SummaryResult(
                capped,
                UsedModel: false,
                Tokens: _counter.Count(capped),
                Error: validation.Reason);
        }

        return new SummaryResult(capped, UsedModel: true, _counter.Count(capped), Error: null);
    }
}
