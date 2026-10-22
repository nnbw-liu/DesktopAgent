using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatRoleModel = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 单一上下文入口（总纲 §6.1）：任何请求的系统提示词、技能、历史消息、附件、工具轨迹
/// 都在这里预算、裁剪并产出 <see cref="ContextPlan"/>。UI / 插件 / 工具都不得另拼消息列表。
/// </summary>
public sealed class ContextBuilder
{
    private readonly ITokenCounter _counter;
    private readonly ISettingsService _settings;
    private readonly TokenCalibrationStore? _calibration;
    private readonly ModelProfileResolver _resolver;
    private readonly ContextTrimmer _trimmer = new();

    public ContextBuilder(
        ITokenCounter counter,
        ISettingsService settings,
        TokenCalibrationStore? calibration = null)
    {
        _counter = counter;
        _settings = settings;
        _calibration = calibration;
        _resolver = new ModelProfileResolver(calibration);
    }

    public ITokenCounter Counter => _counter;

    /// <summary>解析模型档案（供 UI / 报告在 Build 之前展示窗口）。</summary>
    public ModelProfile ResolveProfile(ContextBuildRequest request) =>
        _resolver.Resolve(request.Provider, request.ModelId, FindEntry(request.Provider, request.ModelId));

    /// <summary>组装一次请求的上下文；超预算且无法再裁时抛 <see cref="ContextBudgetException"/>。</summary>
    public ContextPlan Build(ContextBuildRequest request)
    {
        var settings = _settings.Current.Context ?? new ContextSettings();
        var profile = ResolveProfile(request);
        var policy = new ContextPolicy(profile, settings);
        var budget = Math.Abs(request.BudgetScale - 1.0) > 1e-9 && request.BudgetScale > 0
            ? policy.Budget.Scale(request.BudgetScale)
            : policy.Budget;

        var calibrationFactor = _calibration?.GetFactor(request.Provider.Id, request.ModelId) ?? 1.0;

        var (instructions, systemTokens, skillTokens, skillTruncated) =
            BuildInstructions(request, budget, calibrationFactor);

        // 插件 Run：不做预算裁剪（输入上限由 PluginRunner 单独把关），但同样经过这里，
        // 保证「所有请求只从 ContextBuilder 出去」且报告口径一致。
        if (request.Passthrough)
        {
            return BuildPassthrough(request, profile, budget, calibrationFactor, instructions);
        }

        var messageBudget = Math.Max(128, budget.Total - systemTokens - skillTokens);
        var trimOptions = TrimOptions.FromPolicy(
            policy,
            messageBudget,
            _counter,
            calibrationFactor,
            request.PinnedMessageIds);

        var trim = _trimmer.Trim(request.Messages, trimOptions, AgentMessageMapper.Render);

        var aiMessages = trim.Messages
            .Select(entry => new AIChatMessage(
                entry.Source.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
                entry.OmissionNote is null
                    ? entry.Rendered.Text
                    : entry.OmissionNote + "\n" + entry.Rendered.Text))
            .ToList();

        var stats = trim.Stats;
        var segments = CalibrateSegments(stats, systemTokens, skillTokens, calibrationFactor);
        var estimated = segments.Total;
        var rawEstimated = systemTokens + skillTokens + stats.MessageTokens;

        var report = new ContextReport(
            request.Provider.Id,
            request.ModelId,
            budget.Window,
            budget.ReservedOutput,
            budget.Safety,
            budget.Total,
            budget.FixedCap,
            Estimated: estimated,
            RawEstimated: rawEstimated,
            CalibrationFactor: calibrationFactor,
            Segments: segments,
            OmittedMessages: stats.OmittedMessages,
            OmittedTokens: (int)Math.Round(stats.OmittedTokens * calibrationFactor),
            KeptTurns: stats.KeptTurns,
            TotalTurns: stats.TotalTurns,
            PinnedCount: stats.PinnedCount,
            PinnedTruncated: stats.PinnedTruncated,
            AttachmentsStubbed: stats.AttachmentsStubbed,
            Trimmed: stats.Trimmed || skillTruncated,
            Passthrough: false);

        if (estimated > budget.Total)
        {
            var message =
                $"上下文超出预算：估算 {FormatTokens(estimated)} token / 预算 {FormatTokens(budget.Total)}，"
                + $"窗口 {FormatTokens(budget.Window)}。P1 的确定性裁剪已到极限，P2 会在这里接入摘要压缩；"
                + "临时办法：新建会话、减少附件，或在设置里把该模型的窗口调大。";

            throw new ContextBudgetException(AgentErrorKind.NeedsCompaction, message, report);
        }

        return new ContextPlan(
            request.Provider.Id,
            request.ModelId,
            profile,
            budget,
            instructions,
            aiMessages,
            report);
    }

    private ContextPlan BuildPassthrough(
        ContextBuildRequest request,
        ModelProfile profile,
        ContextBudget budget,
        double calibrationFactor,
        string instructions)
    {
        var systemTokens = _counter.CountMessage("system", instructions);
        var messages = new List<AIChatMessage>(request.Messages.Count);
        var turnTokens = 0;
        var attachmentTokens = 0;
        var traceTokens = 0;
        var pinnedTokens = 0;

        foreach (var message in request.Messages)
        {
            var rendered = AgentMessageMapper.Render(message, int.MaxValue, RenderTrace(message));

            messages.Add(new AIChatMessage(
                message.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
                rendered.Text));

            var total = _counter.CountMessage(message.Role.ToString(), rendered.Text);
            var attachment = rendered.AttachmentBlock is null
                ? 0
                : _counter.CountMessage(message.Role.ToString(), rendered.AttachmentBlock);
            var trace = rendered.TraceText is null
                ? 0
                : _counter.CountMessage(message.Role.ToString(), rendered.TraceText);

            if (message.IsPinned)
            {
                pinnedTokens += total;
            }
            else
            {
                attachmentTokens += Math.Max(0, attachment);
                traceTokens += Math.Max(0, trace);
                turnTokens += Math.Max(0, total - attachment - trace);
            }
        }

        var raw = systemTokens + turnTokens + attachmentTokens + traceTokens + pinnedTokens;
        var segments = CalibrateSegments(
            new TrimStats(0, budget.Total, 0, 0, turnTokens, attachmentTokens, traceTokens, pinnedTokens,
                0, 0, 0, request.Messages.Count, request.Messages.Count, 0, false, false, false),
            systemTokens,
            0,
            calibrationFactor);

        var report = new ContextReport(
            request.Provider.Id,
            request.ModelId,
            budget.Window,
            budget.ReservedOutput,
            budget.Safety,
            budget.Total,
            budget.FixedCap,
            Estimated: segments.Total,
            RawEstimated: raw,
            CalibrationFactor: calibrationFactor,
            Segments: segments,
            OmittedMessages: 0,
            OmittedTokens: 0,
            KeptTurns: request.Messages.Count,
            TotalTurns: request.Messages.Count,
            PinnedCount: 0,
            PinnedTruncated: false,
            AttachmentsStubbed: false,
            Trimmed: false,
            Passthrough: true);

        return new ContextPlan(
            request.Provider.Id,
            request.ModelId,
            profile,
            budget,
            instructions,
            messages,
            report);
    }

    private static string? RenderTrace(ChatMessageModel message) =>
        message.ToolTrace.Count == 0
            ? null
            : string.Join('\n', message.ToolTrace
                .TakeLast(ToolTraceFormatter.MaxEntriesPerMessage)
                .Select(ToolTraceFormatter.RenderLine));

    /// <summary>
    /// 固定块：系统提示词 + 可选技能。技能超预算时截断正文并加提示（§3.4），
    /// 而不是让请求失败；系统提示词本身不截断（它是行为契约）。
    /// </summary>
    private (string Instructions, int SystemTokens, int SkillTokens, bool SkillTruncated)
        BuildInstructions(ContextBuildRequest request, ContextBudget budget, double calibrationFactor)
    {
        var baseInstructions = request.InstructionsOverride ?? AgentInstructions.Default;
        var systemTokens = _counter.CountMessage("system", baseInstructions);

        if (string.IsNullOrWhiteSpace(request.SkillText))
        {
            return (baseInstructions, systemTokens, 0, false);
        }

        var header = $"\n\n## 本轮使用的技能：{request.SkillName}\n请严格按以下技能说明执行任务：\n\n";
        var skillText = request.SkillText;
        var truncated = false;

        // 允许技能占满固定块剩余额度（按校准后的预算换算回原始估算）。
        var factor = calibrationFactor <= 0 ? 1.0 : calibrationFactor;
        var skillBudgetRaw = (int)Math.Floor(Math.Max(0, budget.FixedCap - systemTokens) / factor);
        var headerTokens = _counter.Count(header);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var tokens = _counter.Count(skillText) + headerTokens;
            if (tokens <= skillBudgetRaw)
            {
                break;
            }

            var ratio = (double)Math.Max(0, skillBudgetRaw - headerTokens) / Math.Max(1, tokens - headerTokens);
            var maxChars = (int)Math.Floor(skillText.Length * Math.Clamp(ratio, 0.05, 0.95));

            if (maxChars <= 0)
            {
                skillText = string.Empty;
            }
            else if (maxChars < skillText.Length)
            {
                skillText = string.Concat(
                    skillText.AsSpan(0, maxChars),
                    "\n…（技能内容过长，已截断；剩余部分本轮未发送）");
            }

            truncated = true;

            if (skillText.Length == 0)
            {
                break;
            }
        }

        if (skillText.Length == 0)
        {
            return (baseInstructions, systemTokens, 0, true);
        }

        var instructions = baseInstructions + header + skillText;
        var skillTokens = Math.Max(0, _counter.CountMessage("system", instructions) - systemTokens);

        return (instructions, systemTokens, skillTokens, truncated);
    }

    private ContextSegments CalibrateSegments(
        TrimStats stats,
        int systemTokens,
        int skillTokens,
        double calibrationFactor)
    {
        var factor = calibrationFactor <= 0 ? 1.0 : calibrationFactor;
        int Scale(int raw) => (int)Math.Round(raw * factor, MidpointRounding.AwayFromZero);

        return new ContextSegments(
            System: Scale(systemTokens),
            Skills: Scale(skillTokens),
            Turns: Scale(stats.TurnTokens),
            Attachments: Scale(stats.AttachmentTokens),
            ToolTrace: Scale(stats.TraceTokens),
            Pinned: Scale(stats.PinnedTokens));
    }

    private static ModelEntry? FindEntry(ModelProvider provider, string modelId) =>
        provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal));

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();
}
