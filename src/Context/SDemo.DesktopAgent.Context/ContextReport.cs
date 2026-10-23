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
    RoutingDecision? Routing = null)
{
    /// <summary>占输入预算的比例（0~1+）。</summary>
    public double Occupancy => Total <= 0 ? 0 : (double)Estimated / Total;

    /// <summary>占模型窗口的比例（UI 里给用户看的「窗口用了多少」）。</summary>
    public double WindowOccupancy => ContextWindow <= 0 ? 0 : (double)Estimated / ContextWindow;

    /// <summary>本轮工具 schema 的 token（分段明细里的 Tools 段）。</summary>
    public int ToolSchemaTokens => Segments.ToolSchema;

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
    int ToolSchema = 0)
{
    public int Total => System + Skills + Turns + Attachments + ToolTrace + Pinned + Summary + StatePack + ToolSchema;
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
    string? StatePackText = null,
    bool SummaryFallback = false,
    AgentModePreference? ModeOverride = null,
    RoutingDecision? Routing = null,
    string? SkillIndexText = null);
