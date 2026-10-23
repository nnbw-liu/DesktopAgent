using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次增量摘要请求：<paramref name="PreviousSummary"/> 是已覆盖较早历史的摘要，
/// <paramref name="Chunk"/> 是本次新增的一段（只喂增量，不重复摘要）。
/// </summary>
public sealed record SummaryRequest(
    string? PreviousSummary,
    IReadOnlyList<ChatMessageModel> Chunk,
    int ChunkStartOrder,
    ModelProvider Provider,
    string ModelId,
    int MaxTokens,
    int InputMaxTokens,
    TimeSpan Timeout);

/// <summary>摘要结果；<see cref="UsedModel"/> 为 false 时 <see cref="Error"/> 说明降级原因。</summary>
public sealed record SummaryResult(string Text, bool UsedModel, int Tokens, string? Error)
{
    public bool Ok => UsedModel && Error is null && !string.IsNullOrWhiteSpace(Text);

    public static SummaryResult Failed(string error) => new(string.Empty, false, 0, error);
}

/// <summary>
/// 摘要器（P2 §3.2）。实现可以失败：调用方（<see cref="ContextCompactor"/>）负责用确定性兜底补位，
/// 因此这里返回失败而不是抛异常（用户取消除外）。
/// </summary>
public interface IContextSummarizer
{
    Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken);
}
