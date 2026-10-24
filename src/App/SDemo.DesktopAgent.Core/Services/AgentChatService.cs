using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Agents;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>流式增量：正文与思维链分开返回。</summary>
/// <param name="Text">正文增量。</param>
/// <param name="Reasoning">思维链增量。</param>
/// <param name="Step">
/// 这一段属于第几次模型请求（P1a）。<c>-1</c> 表示运行时没给出 step 边界（FICC），
/// 界面按「本回合第几轮」兜底。
/// </param>
public readonly record struct AgentDelta(string Text, string Reasoning, int Step = -1);

/// <summary>
/// 一次生成所需的全部输入：已由 <see cref="ContextBuilder"/> 裁剪好的上下文、
/// 本轮启用的工具包、会话与 metrics。插件 Run 与普通对话共用这一条路径。
/// </summary>
public sealed record AgentRunPlan(
    ModelProvider Provider,
    string ModelId,
    ContextPlan Context,
    IReadOnlySet<string> ToolPacks,
    string SessionId = "",
    AgentRunMetrics? Metrics = null,
    ChatSession? Session = null,
    EffectiveSettings? Scope = null);

/// <summary>
/// 通过 Microsoft Agent Framework 调用模型。只负责传输、流式解析与工具轨迹采集；
/// 消息取舍在 <see cref="ContextBuilder"/>，本服务不再决定发什么。
/// </summary>
public interface IAgentChatService
{
    /// <summary>按现成的 plan 执行；<paramref name="telemetry"/> 收集中途裁剪与请求计数。</summary>
    IAsyncEnumerable<AgentDelta> RunAsync(
        AgentRunPlan plan,
        ContextRunTelemetry? telemetry = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentChatService : IAgentChatService
{
    private static readonly Regex ExitCodePattern = new(
        @"exit(?: code)?\s*[:=]?\s*(?<code>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EditLinePattern = new(
        @"^\[EDIT\]\s+(?<path>.+?)\s+\+(?<added>\d+)\s+-(?<removed>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly AgentToolRegistry _tools;
    private readonly WorkspaceContext _workspace;
    private readonly ISettingsService _settings;
    private readonly IToolApprovalService _approvals;
    private readonly ISandboxJobService _sandbox;
    private readonly IToolAuditLog _audit;
    private readonly IAppPaths _paths;
    private readonly IFileReadRegistry _reads;
    private readonly IAgentRuntime _runtime;
    private readonly ISkillLibrary? _skills;
    private readonly IWorkspaceMemory? _memory;

    public AgentChatService(
        AgentToolRegistry tools,
        WorkspaceContext workspace,
        ISettingsService settings,
        IToolApprovalService approvals,
        ISandboxJobService sandbox,
        IToolAuditLog audit,
        IAppPaths paths,
        IFileReadRegistry reads,
        IAgentRuntime runtime,
        ISkillLibrary? skills = null,
        IWorkspaceMemory? memory = null)
    {
        _tools = tools;
        _workspace = workspace;
        _settings = settings;
        _approvals = approvals;
        _sandbox = sandbox;
        _audit = audit;
        _paths = paths;
        _reads = reads;
        _runtime = runtime;
        _skills = skills;
        _memory = memory;
    }

    public async IAsyncEnumerable<AgentDelta> RunAsync(
        AgentRunPlan plan,
        ContextRunTelemetry? telemetry = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        telemetry ??= new ContextRunTelemetry { Plan = plan.Context, Report = plan.Context.Report };

        // R1.4：本次生成期间真正上屏的审批都记到这个 metrics 上（含工具循环里的多次）。
        using var approvalScope = ApprovalScope.Begin(plan.Metrics);

        // 每次 Run 生成一份工具上下文：会话 / 工作区都按这一次生成固定下来，
        // 单例工具包不会把上一个会话的上下文带进来。
        var context = new ToolContext(
            plan.SessionId,
            _workspace.RootPath,
            AgentAttachmentRoots.Build(_paths, plan.SessionId),
            _settings,
            _approvals,
            _sandbox,
            _audit,
            _reads,
            plan.Metrics,
            plan.Session,
            plan.Metrics?.RunId,
            _skills,
            plan.Session?.WorkspaceId,
            _memory,
            plan.Scope);

        var agentSettings = plan.Scope?.Agent ?? _settings.Current.Agent ?? new AgentSettings();
        var available = _tools.ToolNames(plan.ToolPacks);
        var agentMode = Context.AgentModeResolver.Resolve(
            plan.Context.Profile,
            plan.ModelId,
            agentSettings.Mode);
        var decision = plan.Context.Report.Routing ?? ResolveRouting(plan, available, agentSettings, agentMode);

        // M1：每轮只路由一次；工具集合 = 核心集 + 选中包（+ 兜底入口），不再全量灌给模型。
        var (tools, measuredSchemaTokens) = CreateExposedTools(context, plan, decision);

        // M4：把本轮路由记进 metrics，流结束后可算「暴露 N 个 / 实际调用 M 个 / 未用 K 个」。
        plan.Metrics?.RecordRouting(decision);

        // 宿主侧量出真实 schema 后回填报告，UI 的分段明细与预算账本用的是同一次路由。
        telemetry.Report = (telemetry.Report ?? plan.Context.Report)
            .WithRouting(measuredSchemaTokens, decision with { EstimatedSchemaTokens = measuredSchemaTokens });

        if (agentMode == Context.AgentMode.Compact && plan.Metrics is not null)
        {
            plan.Metrics.Guard.Limit = Math.Clamp(agentSettings.CompactMaxToolCalls, 1, 16);
        }

        var toolCallLimit = agentMode == Context.AgentMode.Compact
            ? Math.Clamp(agentSettings.CompactMaxToolCalls, 1, 16)
            : Math.Clamp(agentSettings.FullMaxToolCalls, 1, 16);

        if (plan.Metrics is not null)
        {
            plan.Metrics.Guard.Limit = toolCallLimit;
        }

        var invocation = CreateInvocation(
            plan.Provider,
            plan.ModelId,
            tools,
            plan.Context,
            telemetry,
            toolCallLimit,
            ResolveInstructions(plan, decision),
            plan.Scope);

        var pendingCalls = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var reasoning = new StringBuilder();

        // 累计已经产生的字数：工具调用记录下来，重开会话时才能还原「思考→工具→思考」的顺序。
        var emittedText = 0;
        var emittedReasoning = 0;

        // P1a：内置循环会带来显式的 step；-1 表示这个运行时看不到 step 边界（FICC）。
        var currentStep = -1;

        try
        {
            await foreach (var evt in _runtime.StreamAsync(invocation, cancellationToken))
            {
                var toolChanged = false;

            switch (evt)
            {
                case AgentStreamEvent.Usage usage:
                    plan.Metrics?.AddUsage(usage.InputTokens, usage.OutputTokens);
                    break;

                // 上一步攒下的文本必须按旧 step 先落地，否则最后一句会挂到下一步的序号上。
                case AgentStreamEvent.StepStarted started:
                    if (text.Length > 0 || reasoning.Length > 0)
                    {
                        yield return new AgentDelta(text.ToString(), reasoning.ToString(), currentStep);
                        text.Clear();
                        reasoning.Clear();
                    }

                    currentStep = started.Step;
                    break;

                case AgentStreamEvent.StepCompleted:
                    break;

                case AgentStreamEvent.Reasoning delta:
                    reasoning.Append(delta.Value);
                    emittedReasoning += delta.Value.Length;
                    break;

                case AgentStreamEvent.Text delta:
                    text.Append(delta.Value);
                    emittedText += delta.Value.Length;
                    break;

                case AgentStreamEvent.FunctionCall call:
                    var digest = DigestArguments(call.Arguments);
                    pendingCalls[call.CallId] = new PendingToolCall(
                        call.Name,
                        digest,
                        DescribeArguments(call.Arguments),
                        BuildTitle(call.Name, call.Arguments),
                        DateTimeOffset.Now);

                    // 工具一开始执行就落到轨迹里；结果回来时按 CallId 原地替换成最终状态。
                    plan.Metrics?.BeginToolTrace(new ToolTraceEntry(
                        call.Name,
                        ToolTraceEntry.StatusRunning,
                        null,
                        digest,
                        null,
                        0,
                        DateTime.Now,
                        ReasoningChars: emittedReasoning,
                        TextChars: emittedText,
                        CallId: call.CallId,
                        ArgsJson: pendingCalls[call.CallId].ArgsJson,
                        Title: pendingCalls[call.CallId].Title));
                    toolChanged = true;
                    break;

                case AgentStreamEvent.FunctionResult result:
                    plan.Metrics?.RecordToolTrace(BuildTraceEntry(
                        result.CallId,
                        result.Result,
                        pendingCalls,
                        emittedReasoning,
                        emittedText));
                    toolChanged = true;
                    break;
            }

            if (text.Length > 0 || reasoning.Length > 0 || toolChanged)
            {
                    yield return new AgentDelta(text.ToString(), reasoning.ToString(), currentStep);
                    text.Clear();
                    reasoning.Clear();
                }
            }
        }
        finally
        {
            // 用户停止 / 网络中断时，尚未返回结果的工具不能永远停在「运行中」。
            plan.Metrics?.CancelRunningTools();
        }
    }

    /// <summary>
    /// 插件 Run 等直接构造 <see cref="AgentRunPlan"/> 的路径不会经过 Runner 的路由，
    /// 这里补一次同规则的判定，保证「每轮只路由一次」在两条路径上都成立。
    /// </summary>
    private RoutingDecision ResolveRouting(
        AgentRunPlan plan,
        IReadOnlyList<string> available,
        AgentSettings agentSettings,
        Context.AgentMode mode)
    {
        if (available.Count == 0)
        {
            return RoutingDecision.Empty;
        }

        var userInput = plan.Context.Messages
            .LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        var recent = plan.Metrics?.ToolTrace.Select(entry => entry.ToolId).TakeLast(6).ToArray() ?? [];

        return CapabilityRouter.Route(new CapabilityRoutingInput(
            userInput,
            recent,
            available,
            mode,
            agentSettings.ResolveMaxExposedTools(plan.Context.Profile.ContextWindow, mode),
            agentSettings.SearchCapabilitiesEnabled,
            agentSettings.ExposeAllTools));
    }

    /// <summary>
    /// 按路由结果生成工具，并在宿主侧量出真实 schema token。
    /// 超出窗口预算（8K 1.5K / 32K 3K）时从路由尾部丢弃，兜底入口最后才丢。
    /// </summary>
    private (IReadOnlyList<AITool> Tools, int SchemaTokens) CreateExposedTools(
        ToolContext context,
        AgentRunPlan plan,
        RoutingDecision decision)
    {
        if (decision.Count == 0)
        {
            return ([], 0);
        }

        var exposed = new HashSet<string>(decision.ExposedTools, StringComparer.Ordinal);
        var tools = _tools
            .CreateExposed(context, plan.ToolPacks, exposed, plan.Metrics, decision.EscapeHatch)
            .ToList();

        var counter = new HeuristicTokenCounter();
        var limit = Math.Min(plan.Context.Budget.ToolSchemaLimit, plan.Context.Budget.Total);
        var tokens = MeasureToolSchema(tools, counter);

        while (tokens > limit && tools.Count > 1)
        {
            var index = tools.FindLastIndex(tool =>
                !string.Equals(tool.Name, CapabilityRouter.EscapeHatchTool, StringComparison.Ordinal));

            if (index < 0)
            {
                break;
            }

            tools.RemoveAt(index);
            tokens = MeasureToolSchema(tools, counter);
        }

        return (tools, tokens);
    }

    /// <summary>工具 schema 的实测成本：名称 + 描述 + 参数 JSON，走与其他上下文相同的估算器。</summary>
    private static int MeasureToolSchema(IReadOnlyList<AITool> tools, ITokenCounter counter)
    {
        var total = 0;

        foreach (var tool in tools)
        {
            var schema = tool is AIFunction function ? function.JsonSchema.GetRawText() : string.Empty;
            total += counter.CountMessage("tool", $"{tool.Name}\n{tool.Description}\n{schema}");
        }

        return total;
    }

    /// <summary>
    /// 把 provider 配置映射成一次运行时调用。中间裁剪层以 <c>Decorate</c> 注入：
    /// FICC 由运行时套在外层，每次 LLM 请求都会经过装饰器，因此能在工具循环内部裁剪（§3.7 分支 A）。
    /// </summary>
    /// <summary>
    /// 直连构造 plan 的路径（插件 / 旧调用点）没有经过 Runner 路由：如果这里补判出「对话轮」，
    /// 提示词也要换成对话版，否则会出现「没有工具、提示词却要求每步调用工具」的矛盾。
    /// </summary>
    private static string ResolveInstructions(AgentRunPlan plan, RoutingDecision decision) =>
        decision.NeedsTools || plan.Context.Report.Routing is not null
            ? plan.Context.Instructions
            : SystemPrompts.Chat;

    private AgentInvocation CreateInvocation(
        ModelProvider provider,
        string modelId,
        IReadOnlyList<AITool> tools,
        ContextPlan plan,
        ContextRunTelemetry telemetry,
        int toolCallLimit,
        string instructions,
        EffectiveSettings? scope)
    {
        var settings = scope?.Context ?? _settings.Current.Context ?? new ContextSettings();
        var agent = scope?.Agent ?? _settings.Current.Agent ?? new AgentSettings();
        var budgetOptions = new ContextBudgetChatOptions(
            Enabled: settings.Enabled && settings.MidTurnTrim,
            Budget: plan.Budget,
            Counter: new HeuristicTokenCounter(),
            CalibrationFactor: plan.Report.CalibrationFactor,
            KeepRecentTurns: settings.KeepRecentTurns,
            ToolResultChars: settings.MidTurnToolResultChars,
            MaxOutputTokens: Math.Clamp(plan.Profile.MaxOutputTokens, 128, 8192));

        return new AgentInvocation(
            new Uri(provider.BaseUrl, UriKind.Absolute),
            provider.ApiKey,
            modelId,
            instructions,
            plan.Messages,
            tools,
            // 允许的次数 + 2 次收尾，和小模型被 Block 后的收尾行为保持一致。
            toolCallLimit + 2,
            Decorate: inner => new ContextBudgetChatClient(inner, budgetOptions, telemetry),
            // 模型档案里配了思考强度才发送 reasoning_effort（none = 显式关闭）；没配就保持服务端默认。
            ReasoningEffort: plan.Profile.ReasoningEffort,
            // P1a：工具循环由谁跑，只在设置里显式打开时换成内置循环（默认仍是 FICC）。
            NativeLoop: agent.NativeLoop);
    }

    private static ToolTraceEntry BuildTraceEntry(
        string callId,
        string? text,
        IReadOnlyDictionary<string, PendingToolCall> pending,
        int reasoningChars = -1,
        int textChars = -1)
    {
        var hasCall = pending.TryGetValue(callId, out var call);
        var name = hasCall ? call!.Name : "tool";
        var digest = hasCall ? call!.Digest : null;

        var (status, exitCode) = Classify(text);

        return new ToolTraceEntry(
            name,
            status,
            exitCode,
            digest,
            Summarize(text),
            text?.Length ?? 0,
            DateTime.Now,
            ParseEdits(text),
            reasoningChars,
            textChars,
            callId,
            ArgsJson: hasCall ? call!.ArgsJson : null,
            Title: hasCall ? call!.Title : null,
            DurationMs: hasCall
                ? (int)Math.Max(0, (DateTimeOffset.Now - call!.StartedAt).TotalMilliseconds)
                : -1,
            ResultPreview: Preview(text));
    }

    /// <summary>一次已发出、尚未拿到结果的工具调用；用于在结果回来时补齐耗时与展示信息。</summary>
    private sealed record PendingToolCall(
        string Name,
        string? Digest,
        string? ArgsJson,
        string? Title,
        DateTimeOffset StartedAt);

    /// <summary>工具卡的标题：优先取最有辨识度的参数，其次退回工具名。</summary>
    internal static string? BuildTitle(string name, IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        static string? Read(IDictionary<string, object?> source, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (source.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text)
                {
                    return text;
                }
            }

            return null;
        }

        var title = name switch
        {
            "run_command" or "run_powershell" => ComposeCommand(arguments),
            "read_file" or "write_file" or "replace_text" or "apply_patch" or "list_tree" or "list_dir"
                => Read(arguments, "path", "file", "directory"),
            "search_files" or "glob_files" => Read(arguments, "pattern", "query"),
            "web_search" => Read(arguments, "query", "q"),
            "load_skill" => Read(arguments, "name"),
            "todo_write" => null,
            _ => Read(arguments, "path", "file", "query", "pattern", "command")
        };

        return title is null ? null : Clip(title, 120);
    }

    /// <summary>命令类工具的标题：命令 + 最多两个参数（够看出在构建什么，不必背整行脚本）。</summary>
    private static string? ComposeCommand(IDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("command", out var command) || command?.ToString() is not { Length: > 0 } executable)
        {
            return null;
        }

        if (!arguments.TryGetValue("args", out var args) || args?.ToString() is not { Length: > 0 } rest)
        {
            return executable;
        }

        var head = string.Join(' ', rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));

        return head.Length == 0 ? executable : $"{executable} {head}";
    }

    /// <summary>参数原文（截断到 512 字符）：只给界面算卡片，不进入模型上下文。</summary>
    internal static string? DescribeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        try
        {
            return Clip(JsonSerializer.Serialize(arguments), 512);
        }
        catch (NotSupportedException)
        {
            // 参数里有不可序列化的值：宁可不给卡片，也不要打断工具调用。
            return null;
        }
    }

    /// <summary>结果预览（首 200 字符），供工具卡展开时先看。</summary>
    internal static string? Preview(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Clip(text, 200);

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");

    /// <summary>从工具结果末尾的 <c>[EDIT] 路径 +N -M</c> 行汇总本次改动，供消息下方展示。</summary>
    internal static IReadOnlyList<EditedFile>? ParseEdits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("[EDIT]", StringComparison.Ordinal))
        {
            return null;
        }

        var edits = new List<EditedFile>();
        foreach (Match match in EditLinePattern.Matches(text))
        {
            if (!int.TryParse(match.Groups["added"].Value, out var added)
                || !int.TryParse(match.Groups["removed"].Value, out var removed))
            {
                continue;
            }

            edits.Add(new EditedFile(match.Groups["path"].Value.Trim(), added, removed));
        }

        return edits.Count == 0 ? null : edits;
    }

    /// <summary>
    /// 轨迹的状态只看工具返回文本的前缀 / 退出码，不解析业务语义。
    /// 工具结果被 InstrumentedAIFunction 包在标记里，这里先去掉标记再判断。
    /// </summary>
    internal static (string Status, int? ExitCode) Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (ToolTraceEntry.StatusError, null);
        }

        var body = text;
        var begin = body.IndexOf("[TOOL_RESULT_BEGIN:", StringComparison.Ordinal);
        if (begin >= 0)
        {
            var newline = body.IndexOf('\n', begin);
            if (newline >= 0)
            {
                body = body[(newline + 1)..];
            }
        }

        var close = body.IndexOf("[TOOL_RESULT_END]", StringComparison.Ordinal);
        if (close >= 0)
        {
            body = body[..close];
        }

        body = body.Trim();

        // 工具包装器只在失败 / 被拒绝时写状态标记；成功结果没有标记，保持旧文本的 token 成本。
        if (body.StartsWith("[TOOL_STATUS:", StringComparison.Ordinal))
        {
            var end = body.IndexOf(']');
            if (end > 0)
            {
                var status = body["[TOOL_STATUS:".Length..end].Trim();
                body = body[(end + 1)..].Trim();

                if (string.Equals(status, ToolTraceEntry.StatusRejected, StringComparison.OrdinalIgnoreCase))
                {
                    return (ToolTraceEntry.StatusRejected, null);
                }

                if (string.Equals(status, ToolTraceEntry.StatusError, StringComparison.OrdinalIgnoreCase))
                {
                    return (ToolTraceEntry.StatusError, null);
                }
            }
        }

        if (body.Contains("用户拒绝", StringComparison.Ordinal)
            || body.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return (ToolTraceEntry.StatusDenied, null);
        }

        if (body.Contains("未写入任何文件", StringComparison.Ordinal)
            || body.Contains("未替换任何内容", StringComparison.Ordinal)
            || body.Contains("疑似整文件重写丢失内容", StringComparison.Ordinal))
        {
            return (ToolTraceEntry.StatusRejected, null);
        }

        var exitMatch = ExitCodePattern.Match(body);
        var exitCode = exitMatch.Success && int.TryParse(exitMatch.Groups["code"].Value, out var code)
            ? code
            : (int?)null;

        if (exitCode > 0
            || body.StartsWith("错误", StringComparison.Ordinal)
            || body.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("执行失败", StringComparison.Ordinal))
        {
            return (ToolTraceEntry.StatusError, exitCode);
        }

        return (ToolTraceEntry.StatusOk, exitCode);
    }

    /// <summary>摘要：首 200 + 尾 200 字符，总长 ≤ 400（§3.6，单条 JSON ≤ 2KB）。</summary>
    internal static string? Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const int head = 200;
        const int tail = 200;

        if (text.Length <= ToolTraceFormatter.MaxSummaryChars)
        {
            return text;
        }

        return string.Concat(
            text.AsSpan(0, head),
            " … ",
            text.AsSpan(text.Length - tail, tail));
    }

    /// <summary>参数摘要：路径参数直接留路径，其余用参数 JSON 的 hash，不把脚本正文存进数据库。</summary>
    internal static string? DigestArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        foreach (var key in new[] { "path", "file", "pattern" })
        {
            if (arguments.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } path)
            {
                return path.Length <= 120 ? path : string.Concat(path.AsSpan(0, 120), "…");
            }
        }

        var json = JsonSerializer.Serialize(arguments);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8];

        return $"sha256:{hash.ToLowerInvariant()}（{json.Length} 字符）";
    }
}
