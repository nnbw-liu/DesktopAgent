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
        var mode = AgentModeResolver.Resolve(
            profile,
            request.ModelId,
            request.ModeOverride ?? _settings.Current.Agent.Mode);
        var policy = new ContextPolicy(profile, settings);
        var budget = Math.Abs(request.BudgetScale - 1.0) > 1e-9 && request.BudgetScale > 0
            ? policy.Budget.Scale(request.BudgetScale)
            : policy.Budget;

        var calibrationFactor = _calibration?.GetFactor(request.Provider.Id, request.ModelId) ?? 1.0;

        // M1：工具 schema 也要占预算。路由在进入上下文前给出粗估，宿主拿到真实工具后回填精确值。
        var toolSchemaTokens = request.Routing is null
            ? 0
            : Math.Clamp(request.Routing.EstimatedSchemaTokens, 0, budget.ToolSchemaLimit);

        var (instructions, systemTokens, skillTokens, skillTruncated) =
            BuildInstructions(request, budget, calibrationFactor, mode);

        // 插件 Run：不做预算裁剪（输入上限由 PluginRunner 单独把关），但同样经过这里，
        // 保证「所有请求只从 ContextBuilder 出去」且报告口径一致。
        if (request.Passthrough)
        {
            return BuildPassthrough(request, profile, budget, calibrationFactor, instructions);
        }

        // P2：滚动摘要 + 工作区状态包进入固定块；两者各自计量，先于历史消息分配预算。
        var injection = BuildInjection(request, budget, settings, systemTokens, skillTokens);
        var finalInstructions = injection.Text.Length == 0
            ? instructions
            : instructions + injection.Text;

        var messageBudget = Math.Max(
            128,
            budget.Total - systemTokens - skillTokens - injection.SummaryTokens - injection.StateTokens - toolSchemaTokens);
        var trimOptions = TrimOptions.FromPolicy(
            policy,
            messageBudget,
            _counter,
            calibrationFactor,
            request.PinnedMessageIds,
            request.SummaryThroughOrder);

        var trim = _trimmer.Trim(request.Messages, trimOptions, AgentMessageMapper.Render);

        var aiMessages = trim.Messages
            .Select(entry => new AIChatMessage(
                entry.Source.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
                entry.OmissionNote is null
                    ? entry.Rendered.Text
                    : entry.OmissionNote + "\n" + entry.Rendered.Text))
            .ToList();

        var stats = trim.Stats;
        var segments = CalibrateSegments(
            stats,
            systemTokens,
            skillTokens,
            calibrationFactor,
            injection.SummaryTokens,
            injection.StateTokens,
            toolSchemaTokens);
        var estimated = segments.Total;
        var rawEstimated = systemTokens + skillTokens + stats.MessageTokens
            + injection.SummaryTokens + injection.StateTokens + toolSchemaTokens;

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
            Passthrough: false,
            SummaryUsed: injection.SummaryTokens > 0,
            SummaryTokens: injection.SummaryTokens,
            CompactedMessages: stats.CompactedMessages,
            SummaryFallback: injection.SummaryTokens > 0 && request.SummaryFallback,
            StatePackUsed: injection.StateTokens > 0,
            StatePackTokens: injection.StateTokens,
            Routing: request.Routing);

        if (estimated > budget.Total)
        {
            var message =
                $"上下文超出预算：估算 {FormatTokens(estimated)} token / 预算 {FormatTokens(budget.Total)}，"
                + $"窗口 {FormatTokens(budget.Window)}。确定性裁剪与摘要仍不足；"
                + "临时办法：新建会话、减少附件，或在设置里把该模型的窗口调大。";

            throw new ContextBudgetException(AgentErrorKind.NeedsCompaction, message, report);
        }

        return new ContextPlan(
            request.Provider.Id,
            request.ModelId,
            profile,
            budget,
            finalInstructions,
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
        BuildInstructions(
            ContextBuildRequest request,
            ContextBudget budget,
            double calibrationFactor,
            AgentMode mode)
    {
        var baseInstructions = request.InstructionsOverride
                               ?? (mode == AgentMode.Compact ? SystemPrompts.Compact : SystemPrompts.Full);
        var systemTokens = _counter.CountMessage("system", baseInstructions);

        // M2：技能索引先进入固定块（只截断索引，不因此丢掉手选技能的全文）。
        var instructions = baseInstructions;
        var indexTokens = 0;

        if (!string.IsNullOrWhiteSpace(request.SkillIndexText))
        {
            instructions += TruncateBlock(request.SkillIndexText!, budget.SkillIndexLimit, "技能索引已截断");
            indexTokens = Math.Max(0, _counter.CountMessage("system", instructions) - systemTokens);
        }

        if (string.IsNullOrWhiteSpace(request.SkillText))
        {
            return (instructions, systemTokens, indexTokens, false);
        }

        var header = $"\n\n## 本轮使用的技能：{request.SkillName}\n请严格按以下技能说明执行任务：\n\n";
        var skillText = request.SkillText;
        var truncated = false;

        // 允许技能占满固定块剩余额度（按校准后的预算换算回原始估算），索引先占掉自己那一段。
        var factor = calibrationFactor <= 0 ? 1.0 : calibrationFactor;
        var skillBudgetRaw = (int)Math.Floor(Math.Max(0, budget.FixedCap - systemTokens - indexTokens) / factor);
        if (mode == AgentMode.Compact)
        {
            // 600 是技能段硬上限；留 24 token 给截断提示与消息开销，避免收敛后轻微溢出。
            skillBudgetRaw = Math.Min(skillBudgetRaw, Math.Max(0, 600 - indexTokens - 24));
        }

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
            return (instructions, systemTokens, indexTokens, true);
        }

        instructions += header + skillText;
        var skillTokens = Math.Max(0, _counter.CountMessage("system", instructions) - systemTokens);

        return (instructions, systemTokens, skillTokens, truncated);
    }

    private ContextSegments CalibrateSegments(
        TrimStats stats,
        int systemTokens,
        int skillTokens,
        double calibrationFactor,
        int summaryTokens = 0,
        int statePackTokens = 0,
        int toolSchemaTokens = 0)
    {
        var factor = calibrationFactor <= 0 ? 1.0 : calibrationFactor;
        int Scale(int raw) => (int)Math.Round(raw * factor, MidpointRounding.AwayFromZero);

        return new ContextSegments(
            System: Scale(systemTokens),
            Skills: Scale(skillTokens),
            Turns: Scale(stats.TurnTokens),
            Attachments: Scale(stats.AttachmentTokens),
            ToolTrace: Scale(stats.TraceTokens),
            Pinned: Scale(stats.PinnedTokens),
            Summary: Scale(summaryTokens),
            StatePack: Scale(statePackTokens),
            ToolSchema: Scale(toolSchemaTokens));
    }

    /// <summary>
    /// 摘要与状态包的固定块（P2 §3.2 / §3.3）。先保证摘要（压缩的主产物），
    /// 状态包用剩余额度；两者各自带标题，来源与计量都在 <see cref="ContextSegments"/> 里。
    /// </summary>
    private InjectionBlock BuildInjection(
        ContextBuildRequest request,
        ContextBudget budget,
        ContextSettings settings,
        int systemTokens,
        int skillTokens)
    {
        var hasSummary = !string.IsNullOrWhiteSpace(request.SummaryText);
        var hasState = !string.IsNullOrWhiteSpace(request.StatePackText);

        if (!hasSummary && !hasState)
        {
            return InjectionBlock.Empty;
        }

        // 给历史消息留出至少 128 token：注入块不能把裁剪器逼进「什么都放不下」。
        var remaining = Math.Max(0, budget.Total - systemTokens - skillTokens - 128);
        if (remaining < 64)
        {
            return InjectionBlock.Empty;
        }

        var text = string.Empty;
        var summaryTokens = 0;
        var statePackTokens = 0;

        if (hasSummary)
        {
            var summaryCap = Math.Min(
                Math.Clamp(settings.SummaryMaxTokens, 128, 4096),
                Math.Max(0, remaining * 2 / 3));

            if (summaryCap >= 64)
            {
                var body = SummaryFormat.Cap(request.SummaryText, summaryCap, _counter);
                var block = SummaryHeader + body;
                summaryTokens = _counter.CountMessage("system", block);
                text += block;
            }
        }

        if (hasState)
        {
            var small = budget.Window <= 8192;
            var configured = small
                ? settings.StatePackSmallModelTokenBudget
                : settings.StatePackTokenBudget;

            var stateCap = Math.Min(
                Math.Clamp(configured, 64, 2048),
                Math.Max(0, remaining - summaryTokens));

            if (stateCap >= 64)
            {
                var body = TruncateBlock(request.StatePackText!, stateCap, "状态包已截断");
                var block = StateHeader + body;
                statePackTokens = _counter.CountMessage("system", block);
                text += block;
            }
        }

        return new InjectionBlock(text, summaryTokens, statePackTokens);
    }

    /// <summary>按 token 上限截断任意文本块（状态包专用：超限时保留前面的结构部分）。</summary>
    private string TruncateBlock(string text, int maxTokens, string note)
    {
        var normalized = text.Trim();

        if (_counter.CountMessage("system", normalized) <= maxTokens)
        {
            return normalized;
        }

        var suffix = $"\n…（{note}）";
        var low = 0;
        var high = normalized.Length;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = normalized[..mid] + suffix;

            if (_counter.CountMessage("system", candidate) <= maxTokens)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return string.Concat(normalized.AsSpan(0, low).TrimEnd(), suffix);
    }

    private const string SummaryHeader =
        "\n\n## 已压缩的历史（模型不再看到原文；需要细节时请用工具重新读取）\n";

    private const string StateHeader = "\n\n## 工作区状态（宿主生成，不是用户输入）\n";

    private sealed record InjectionBlock(string Text, int SummaryTokens, int StateTokens)
    {
        public static InjectionBlock Empty { get; } = new(string.Empty, 0, 0);
    }

    private static ModelEntry? FindEntry(ModelProvider provider, string modelId) =>
        provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal));

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();
}
