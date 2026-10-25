using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次增量摘要请求：<paramref name="PreviousSummary"/> 是已覆盖较早历史的摘要，
/// <paramref name="Chunk"/> 是本次新增的一段（只喂增量，不重复摘要）。
/// </summary>
/// <param name="Instructions">
/// 本次请求的系统提示词（P2b §3.3.2 第二条：热前缀第一段）。为 null 时用摘要器自己的固定提示词——
/// 那条路径不是「原对话的真前缀」，缓存用不上，只在拿不到会话提示词时兜底。
/// </param>
/// <param name="Tools">
/// 本轮请求声明的工具（热前缀第二段）。摘要不执行工具，带上它们只为让前缀与对话请求一致；
/// 个别模型看到工具会去调用，<see cref="LlmContextSummarizer"/> 会在没拿到文本时去掉工具重试一次。
/// </param>
public sealed record SummaryRequest(
    string? PreviousSummary,
    IReadOnlyList<ChatMessageModel> Chunk,
    int ChunkStartOrder,
    ModelProvider Provider,
    string ModelId,
    int MaxTokens,
    int InputMaxTokens,
    TimeSpan Timeout,
    string? Instructions = null,
    IReadOnlyList<AITool>? Tools = null,
    int AttachmentCharLimit = 0)
{
    /// <summary>工具声明的 token 成本（与对话侧同一个估算口径），没有工具时为 0。</summary>
    public int ToolSchemaTokens(ITokenCounter counter)
    {
        if (Tools is not { Count: > 0 } tools)
        {
            return 0;
        }

        var total = 0;

        foreach (var tool in tools)
        {
            var schema = tool is AIFunction function ? function.JsonSchema.GetRawText() : string.Empty;
            total += counter.CountMessage("tool", $"{tool.Name}\n{tool.Description}\n{schema}");
        }

        return total;
    }
}

/// <summary>
/// 摘要结果；<see cref="UsedModel"/> 为 false 时 <see cref="Error"/> 说明降级原因。
/// <see cref="InputTokens"/> / <see cref="OutputTokens"/> 是这次摘要请求的 token 成本（R1.4）：
/// 摘要不写会话消息，只有把它们带回去，用户才能在分解面板看到「压缩花了多少」。
/// </summary>
public sealed record SummaryResult(
    string Text,
    bool UsedModel,
    int Tokens,
    string? Error,
    int InputTokens = 0,
    int OutputTokens = 0)
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
