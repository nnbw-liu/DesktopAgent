using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 上下文报告：一次 <see cref="ContextBuilder.Build"/> 的完整预算结果。
/// UI 只读数值（不每帧重算）；P2 的压缩流程也会读它判断是否超限。
/// </summary>
public sealed record ContextReport(
    string ProviderId,
    string ModelId,
    int ContextWindow,
    int ReservedOutput,
    int Safety,
    int Total,
    int FixedCap,
    int Estimated,
    int RawEstimated,
    double CalibrationFactor,
    ContextSegments Segments,
    int OmittedMessages,
    int OmittedTokens,
    int KeptTurns,
    int TotalTurns,
    int PinnedCount,
    bool PinnedTruncated,
    bool AttachmentsStubbed,
    bool Trimmed,
    bool Passthrough = false,
    bool MidTurnTrimmed = false,
    bool OverflowRetried = false,
    int? ActualInputTokens = null,
    int? LearnedWindow = null,
    bool SummaryUsed = false,
    int SummaryTokens = 0,
    int CompactedMessages = 0,
    bool SummaryFallback = false,
    bool StatePackUsed = false,
    int StatePackTokens = 0,
    RoutingDecision? Routing = null,
    int SummaryInputTokens = 0,
    int SummaryOutputTokens = 0,
    IReadOnlyList<ContextSectionUsage>? Sections = null,
    int CacheHitTokens = 0,
    int PrunedTokens = 0,
    // P2c：超长工具输出落盘的条数；SpillFailures 是写盘失败、退回纯截断的条数。
    int SpilledResults = 0,
    int SpillFailures = 0)
{
    /// <summary>占输入预算的比例（0~1+）。</summary>
    public double Occupancy => Total <= 0 ? 0 : (double)Estimated / Total;

    /// <summary>占模型窗口的比例（UI 里给用户看的「窗口用了多少」）。</summary>
    public double WindowOccupancy => ContextWindow <= 0 ? 0 : (double)Estimated / ContextWindow;

    /// <summary>
    /// R1.4：本轮摘要请求花掉的 token（输入 + 输出）。摘要不写会话消息，
    /// 只能靠报告的这两个字段让用户看到「压缩成本」。
    /// </summary>
    public int SummaryCostTokens => SummaryInputTokens + SummaryOutputTokens;

    /// <summary>
    /// P2a：宿主上下文快照的每段归因（名字 / token / 是否 required / 是否被截断）。
    /// 快照段是唯一事实源，界面分解面板直接读它，不再自己分类。
    /// </summary>
    public IReadOnlyList<ContextSectionUsage> SectionUsages => Sections ?? [];

    /// <summary>
    /// P2a：服务端上报的缓存命中输入 token（<c>UsageDetails.CachedInputTokenCount</c>）。
    /// 端点不报就是 0 —— 界面据此决定显不显示命中率，不给假数字。
    /// </summary>
    public double CacheHitRate => ActualInputTokens is > 0
        ? Math.Clamp((double)CacheHitTokens / ActualInputTokens.Value, 0, 1)
        : 0;

    /// <summary>本轮工具 schema 的 token（分段明细里的 Tools 段）。</summary>
    public int ToolSchemaTokens => Segments.ToolSchema;

    /// <summary>
    /// P2b：这一轮是否「靠修剪工具轨迹把历史留住了」。
    /// 修剪省了 token 且没有淘汰任何历史，说明整次摘要（模型调用）被跳过。
    /// </summary>
    public bool PrunedInsteadOfCompacted => PrunedTokens > 0 && OmittedMessages == 0;

    /// <summary>M3：工作区知识库索引行的 token（没有归档会话时为 0）。</summary>
    public int KnowledgeTokens => Segments.Knowledge;

    /// <summary>
    /// 宿主侧量出真实工具 schema 后回填报告（M1）。同步修正 <see cref="Estimated"/> /
    /// <see cref="RawEstimated"/>，保持「各分段之和 = 总估算」的不变量。
    /// </summary>
    public ContextReport WithRouting(int toolSchemaTokens, RoutingDecision? routing)
    {
        var clamped = Math.Max(0, toolSchemaTokens);
        var delta = clamped - Segments.ToolSchema;
        var segments = Segments with { ToolSchema = clamped };

        return this with
        {
            Segments = segments,
            Estimated = segments.Total,
            RawEstimated = Math.Max(0, RawEstimated + delta),
            Routing = routing ?? Routing
        };
    }
}

/// <summary>分段明细的 token 数；各段互斥，加总等于报告里的总估算。</summary>
public sealed record ContextSegments(
    int System,
    int Skills,
    int Turns,
    int Attachments,
    int ToolTrace,
    int Pinned,
    int Summary = 0,
    int StatePack = 0,
    int ToolSchema = 0,
    int Knowledge = 0)
{
    public int Total => System + Skills + Turns + Attachments + ToolTrace + Pinned + Summary + StatePack + ToolSchema + Knowledge;
}
/// <summary>
/// 一次请求的产物：系统提示词 + 已裁剪的请求消息 + 报告。
/// <see cref="Services.AgentChatService"/> 只消费它，不再决定消息取舍。
/// </summary>
public sealed record ContextPlan(
    string ProviderId,
    string ModelId,
    ModelProfile Profile,
    ContextBudget Budget,
    string Instructions,
    IReadOnlyList<AIChatMessage> Messages,
    ContextReport Report);

/// <summary>组装上下文的输入。</summary>
public sealed record ContextBuildRequest(
    ModelProvider Provider,
    string ModelId,
    IReadOnlyList<ChatMessageModel> Messages,
    string? SkillName = null,
    string? SkillText = null,
    IReadOnlySet<string>? PinnedMessageIds = null,
    string? InstructionsOverride = null,
    bool Passthrough = false,
    double BudgetScale = 1.0,
    string? SummaryText = null,
    int SummaryThroughOrder = 0,
    WorkspaceState? State = null,
    bool SummaryFallback = false,
    AgentModePreference? ModeOverride = null,
    RoutingDecision? Routing = null,
    string? SkillIndexText = null,
    string? MemoryText = null,
    SDemo.DesktopAgent.Models.EffectiveSettings? Scope = null);
