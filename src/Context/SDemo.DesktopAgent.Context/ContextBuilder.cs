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
    private readonly ContextSectionRegistry _sections;
    private readonly TimeProvider _time;

    public ContextBuilder(
        ITokenCounter counter,
        ISettingsService settings,
        TokenCalibrationStore? calibration = null,
        ContextSectionRegistry? sections = null,
        TimeProvider? time = null)
    {
        _counter = counter;
        _settings = settings;
        _calibration = calibration;
        _resolver = new ModelProfileResolver(calibration);
        _sections = sections ?? ContextSectionRegistry.Default;
        _time = time ?? TimeProvider.System;
    }

    public ITokenCounter Counter => _counter;

    /// <summary>快照段表（P2a）：界面 / 报告 / 测试都读同一份。</summary>
    public ContextSectionRegistry Sections => _sections;

    /// <summary>解析模型档案（供 UI / 报告在 Build 之前展示窗口）。</summary>
    public ModelProfile ResolveProfile(ContextBuildRequest request) =>
        _resolver.Resolve(request.Provider, request.ModelId, FindEntry(request.Provider, request.ModelId));

    /// <summary>组装一次请求的上下文；超预算且无法再裁时抛 <see cref="ContextBudgetException"/>。</summary>
    public ContextPlan Build(ContextBuildRequest request)
    {
        var settings = request.Scope?.Context ?? _settings.Current.Context ?? new ContextSettings();
        var profile = ResolveProfile(request);
        var mode = AgentModeResolver.Resolve(
            profile,
            request.ModelId,
            request.ModeOverride ?? request.Scope?.Agent.Mode ?? _settings.Current.Agent.Mode);
        var policy = new ContextPolicy(profile, settings);
        var budget = Math.Abs(request.BudgetScale - 1.0) > 1e-9 && request.BudgetScale > 0
            ? policy.Budget.Scale(request.BudgetScale)
            : policy.Budget;

        var calibrationFactor = _calibration?.GetFactor(request.Provider.Id, request.ModelId) ?? 1.0;

        // M1：工具 schema 也要占预算。路由在进入上下文前给出粗估，宿主拿到真实工具后回填精确值。
        var toolSchemaTokens = request.Routing is null
            ? 0
            : Math.Clamp(request.Routing.EstimatedSchemaTokens, 0, budget.ToolSchemaLimit);

        var (instructions, systemTokens, skillTokens, knowledgeTokens, skillTruncated) =
            BuildInstructions(request, budget, calibrationFactor, mode, profile.SupportsReasoning);

        // 插件 Run：不做预算裁剪（输入上限由 PluginRunner 单独把关），但同样经过这里，
        // 保证「所有请求只从 ContextBuilder 出去」且报告口径一致。
        if (request.Passthrough)
        {
            return BuildPassthrough(request, profile, budget, calibrationFactor, instructions);
        }

        // P2：滚动摘要仍进固定块；工作区状态改由末尾的「宿主上下文快照」承担（P2a §3.3.1）。
        var injection = BuildInjection(request, budget, settings, systemTokens, skillTokens, knowledgeTokens);
        var finalInstructions = injection.Text.Length == 0
            ? instructions
            : instructions + injection.Text;

        var snapshot = BuildSnapshot(
            request, budget, settings, mode, profile, systemTokens, skillTokens, knowledgeTokens,
            injection.SummaryTokens);

        var messageBudget = Math.Max(
            128,
            budget.Total - systemTokens - skillTokens - knowledgeTokens
            - injection.SummaryTokens - snapshot.Tokens - toolSchemaTokens);
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

        // 快照永远是最后一条：它在裁剪之后追加，所以永远不会被当成「最旧的一轮」淘汰。
        if (snapshot.Text.Length > 0)
        {
            aiMessages.Add(new AIChatMessage(ChatRole.User, snapshot.Text));
        }

        var stats = trim.Stats;
        var segments = CalibrateSegments(
            stats,
            systemTokens,
            skillTokens,
            calibrationFactor,
            injection.SummaryTokens,
            snapshot.Tokens,
            toolSchemaTokens,
            knowledgeTokens);
        var estimated = segments.Total;
        var rawEstimated = systemTokens + skillTokens + knowledgeTokens + stats.MessageTokens
            + injection.SummaryTokens + snapshot.Tokens + toolSchemaTokens;

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
            StatePackUsed: snapshot.Tokens > 0,
            StatePackTokens: snapshot.Tokens,
            Routing: request.Routing,
            Sections: snapshot.Sections,
            PrunedTokens: (int)Math.Round(stats.PrunedTraceTokens * calibrationFactor));

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
    private (string Instructions, int SystemTokens, int SkillTokens, int KnowledgeTokens, bool SkillTruncated)
        BuildInstructions(
            ContextBuildRequest request,
            ContextBudget budget,
            double calibrationFactor,
            AgentMode mode,
            bool supportsReasoning)
    {
        var baseInstructions = request.InstructionsOverride
                               ?? SelectBasePrompt(request.Routing, mode, supportsReasoning);

        // 把「这一轮是什么类型」显式写给模型：提示词里只有规则，路由结果才是本轮的结论。
        // 只有真的要工具时才写；否则会和对话提示词的「不要调用工具」互相打架。
        if (request.InstructionsOverride is null && request.Routing is { NeedsTools: true } routing)
        {
            baseInstructions += IntentHint(routing.Intent, supportsReasoning);
        }

        var systemTokens = _counter.CountMessage("system", baseInstructions);

        // M2：技能索引先进入固定块（只截断索引，不因此丢掉手选技能的全文）。
        var instructions = baseInstructions;
        var indexTokens = 0;

        if (!string.IsNullOrWhiteSpace(request.SkillIndexText))
        {
            instructions += TruncateBlock(request.SkillIndexText!, budget.SkillIndexLimit, "技能索引已截断");
            indexTokens = Math.Max(0, _counter.CountMessage("system", instructions) - systemTokens);
        }

        // M3：工作区知识库只放一句索引；关闭 / 没有归档会话时这里是空的，成本严格为 0。
        var knowledgeTokens = 0;

        if (!string.IsNullOrWhiteSpace(request.MemoryText))
        {
            instructions += TruncateBlock(request.MemoryText!, budget.KnowledgeLimit, "知识库索引已截断");
            knowledgeTokens = Math.Max(
                0,
                _counter.CountMessage("system", instructions) - systemTokens - indexTokens);
        }

        if (string.IsNullOrWhiteSpace(request.SkillText))
        {
            return (instructions, systemTokens, indexTokens, knowledgeTokens, false);
        }

        var header = $"\n\n## 本轮使用的技能：{request.SkillName}\n"
            + "正文已经在本轮提供，不要再调用 load_skill 加载同名技能。请严格按以下技能说明执行任务：\n\n";
        var skillText = request.SkillText;
        var truncated = false;

        // 允许技能占满固定块剩余额度（按校准后的预算换算回原始估算），索引 / 知识库先占掉自己的那一段。
        var factor = calibrationFactor <= 0 ? 1.0 : calibrationFactor;
        var skillBudgetRaw = (int)Math.Floor(
            Math.Max(0, budget.FixedCap - systemTokens - indexTokens - knowledgeTokens) / factor);
        if (mode == AgentMode.Compact)
        {
            // 600 是技能段硬上限；留 24 token 给截断提示与消息开销，避免收敛后轻微溢出。
            skillBudgetRaw = Math.Min(skillBudgetRaw, Math.Max(0, 600 - indexTokens - knowledgeTokens - 24));
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
            return (instructions, systemTokens, indexTokens, knowledgeTokens, true);
        }

        instructions += header + skillText;
        var skillTokens = Math.Max(
            0,
            _counter.CountMessage("system", instructions) - systemTokens - knowledgeTokens);

        return (instructions, systemTokens, skillTokens, knowledgeTokens, truncated);
    }

    /// <summary>
    /// 先看「这一轮要不要工具」：路由判定为对话轮（或没有可用工具）时用
    /// <see cref="SystemPrompts.Chat"/>，否则按窗口 / 模型大小选 Full 或 Compact。
    /// 没有路由结果（插件、单测等旧路径）时保持按模式选择。
    /// </summary>
    /// <param name="supportsReasoning">
    /// 模型有没有思考通道（P1b §3.2.5）：有就不要再逼它在正文里写一行 PLAN。
    /// </param>
    private static string SelectBasePrompt(RoutingDecision? routing, AgentMode mode, bool supportsReasoning)
    {
        if (routing is { NeedsTools: false })
        {
            return SystemPrompts.Chat;
        }

        return mode == AgentMode.Compact
            ? SystemPrompts.Compact
            : SystemPrompts.FullFor(supportsReasoning);
    }

    /// <summary>
    /// 本轮类型的显式声明（只加几行，不重复提示词规则）：
    /// 问答轮先直接回答，任务轮才动手。有思考通道时不再要求 PLAN。
    /// </summary>
    private static string IntentHint(TurnIntent intent, bool supportsReasoning) => intent switch
    {
        TurnIntent.Answer =>
            "\n\n本轮类型：问答。先直接回答；只有需要确认具体文件内容时才调用只读工具，不要顺手改文件或执行命令。",
        TurnIntent.Task => supportsReasoning
            ? "\n\n本轮类型：任务。先在思考里想清步骤再动手；改文件前先读，完成后给出结论。"
            : "\n\n本轮类型：任务。按 PLAN 逐步执行；改文件前先读，完成后给出结论。",
        _ => string.Empty
    };

    private ContextSegments CalibrateSegments(
        TrimStats stats,
        int systemTokens,
        int skillTokens,
        double calibrationFactor,
        int summaryTokens = 0,
        int statePackTokens = 0,
        int toolSchemaTokens = 0,
        int knowledgeTokens = 0)
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
            ToolSchema: Scale(toolSchemaTokens),
            Knowledge: Scale(knowledgeTokens));
    }

    /// <summary>
    /// 压缩摘要的固定块（P2 §3.2）。摘要仍然留在系统提示词里：它是「被压缩掉的历史」的替身，
    /// 只有压缩发生时才会变，所以不会再像状态包那样每轮改一次系统前缀。
    /// </summary>
    private InjectionBlock BuildInjection(
        ContextBuildRequest request,
        ContextBudget budget,
        ContextSettings settings,
        int systemTokens,
        int skillTokens,
        int knowledgeTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(request.SummaryText))
        {
            return InjectionBlock.Empty;
        }

        // 给历史消息留出至少 128 token：注入块不能把裁剪器逼进「什么都放不下」。
        var remaining = Math.Max(0, budget.Total - systemTokens - skillTokens - knowledgeTokens - 128);
        if (remaining < 64)
        {
            return InjectionBlock.Empty;
        }

        var summaryCap = Math.Min(
            Math.Clamp(settings.SummaryMaxTokens, 128, 4096),
            Math.Max(0, remaining * 2 / 3));

        if (summaryCap < 64)
        {
            return InjectionBlock.Empty;
        }

        var body = SummaryFormat.Cap(request.SummaryText, summaryCap, _counter);
        var block = SummaryHeader + body;

        return new InjectionBlock(block, _counter.CountMessage("system", block));
    }

    /// <summary>
    /// 宿主上下文快照（P2a §3.3.1-(2)）：历史末尾的**一条 user 消息**，取代以前拼在
    /// 系统提示词尾部的状态包。三条收益：系统前缀在会话内不再每轮抖动（可缓存）；
    /// 快照贴着当前输入（模型对它的注意力最高）；报告能按段归因。
    /// <para>
    /// 代价照实说：多数 OpenAI 兼容端点不接受中途的 `system` 消息，所以只能用 user + 明确前缀；
    /// 而且「本快照取代之前的快照」这句话必须写进正文，否则多轮之后模型会以为它们都还有效。
    /// </para>
    /// </summary>
    private SnapshotBlock BuildSnapshot(
        ContextBuildRequest request,
        ContextBudget budget,
        ContextSettings settings,
        AgentMode mode,
        ModelProfile profile,
        int systemTokens,
        int skillTokens,
        int knowledgeTokens,
        int summaryTokens)
    {
        // 状态段的总预算还是原来的「状态包上限」，只是它现在管的是一个快照，而不是系统块里的一段。
        var configured = budget.Window <= 8192
            ? settings.StatePackSmallModelTokenBudget
            : settings.StatePackTokenBudget;

        var remaining = Math.Max(
            0,
            budget.Total - systemTokens - skillTokens - knowledgeTokens - summaryTokens - 128);

        // 段的总额度 = min(设置里的状态包上限, 本轮真正剩下的空间)。
        // required 段不受这个数字约束（见 ContextSectionRegistry），所以它只决定可选段能拿多少。
        var sectionCap = Math.Min(Math.Clamp(configured, 64, 2048), remaining);

        var now = _time.GetLocalNow();
        var policy = AgentPolicyFacts.FromSettings(_settings.Current, request.Scope);
        var input = new ContextSectionInput(
            _counter,
            settings,
            mode,
            profile,
            request.State,
            policy,
            now,
            ContextSectionText.ResolveTimeZoneId(TimeZoneInfo.Local),
            LastModelVisibleAt(request));

        var rendered = _sections.Render(input, Math.Max(0, sectionCap));

        if (!rendered.Any)
        {
            return SnapshotBlock.Empty;
        }

        var text = HostSnapshot.Compose(rendered.Text);
        var tokens = _counter.CountMessage("user", text);

        return new SnapshotBlock(text, tokens, rendered.Sections);
    }

    /// <summary>上一条模型可见消息的时间：用它算「距上一条消息多久」，没有历史时是 unavailable。</summary>
    private static DateTimeOffset? LastModelVisibleAt(ContextBuildRequest request)
    {
        if (request.Messages.Count == 0)
        {
            return null;
        }

        var last = request.Messages[^1].CreatedAt;
        return new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Local));
    }

    /// <summary>按 token 上限截断任意文本块（超限时保留前面的结构部分）。</summary>
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

    private sealed record InjectionBlock(string Text, int SummaryTokens)
    {
        public static InjectionBlock Empty { get; } = new(string.Empty, 0);
    }

    private sealed record SnapshotBlock(
        string Text,
        int Tokens,
        IReadOnlyList<ContextSectionUsage> Sections)
    {
        public static SnapshotBlock Empty { get; } = new(string.Empty, 0, []);
    }

    private static ModelEntry? FindEntry(ModelProvider provider, string modelId) =>
        provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal));

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();
}
