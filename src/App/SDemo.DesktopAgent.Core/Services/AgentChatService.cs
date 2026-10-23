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
public readonly record struct AgentDelta(string Text, string Reasoning);

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
    ChatSession? Session = null);

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
        ISkillLibrary? skills = null)
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
    }

    public async IAsyncEnumerable<AgentDelta> RunAsync(
        AgentRunPlan plan,
        ContextRunTelemetry? telemetry = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        telemetry ??= new ContextRunTelemetry { Plan = plan.Context, Report = plan.Context.Report };

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
            _skills);

        var agentSettings = _settings.Current.Agent ?? new AgentSettings();
        var available = _tools.ToolNames(plan.ToolPacks);
        var agentMode = Context.AgentModeResolver.Resolve(
            plan.Context.Profile,
            plan.ModelId,
            agentSettings.Mode);
        var decision = plan.Context.Report.Routing ?? ResolveRouting(plan, available, agentSettings, agentMode);

        // M1：每轮只路由一次；工具集合 = 核心集 + 选中包（+ 兜底入口），不再全量灌给模型。
        var (tools, measuredSchemaTokens) = CreateExposedTools(context, plan, decision);

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

        var invocation = CreateInvocation(plan.Provider, plan.ModelId, tools, plan.Context, telemetry, toolCallLimit);

        var pendingCalls = new Dictionary<string, (string Name, string? Digest)>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var reasoning = new StringBuilder();

        await foreach (var evt in _runtime.StreamAsync(invocation, cancellationToken))
        {
            switch (evt)
            {
                case AgentStreamEvent.Usage usage:
                    plan.Metrics?.AddUsage(usage.InputTokens, usage.OutputTokens);
                    break;

                case AgentStreamEvent.Reasoning delta:
                    reasoning.Append(delta.Value);
                    break;

                case AgentStreamEvent.Text delta:
                    text.Append(delta.Value);
                    break;

                case AgentStreamEvent.FunctionCall call:
                    pendingCalls[call.CallId] = (call.Name, DigestArguments(call.Arguments));
                    break;

                case AgentStreamEvent.FunctionResult result:
                    plan.Metrics?.RecordToolTrace(BuildTraceEntry(result.CallId, result.Result, pendingCalls));
                    break;
            }

            if (text.Length > 0 || reasoning.Length > 0)
            {
                yield return new AgentDelta(text.ToString(), reasoning.ToString());
                text.Clear();
                reasoning.Clear();
            }
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
    private AgentInvocation CreateInvocation(
        ModelProvider provider,
        string modelId,
        IReadOnlyList<AITool> tools,
        ContextPlan plan,
        ContextRunTelemetry telemetry,
        int toolCallLimit)
    {
        var settings = _settings.Current.Context ?? new ContextSettings();
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
            plan.Instructions,
            plan.Messages,
            tools,
            // 允许的次数 + 2 次收尾，和小模型被 Block 后的收尾行为保持一致。
            toolCallLimit + 2,
            Decorate: inner => new ContextBudgetChatClient(inner, budgetOptions, telemetry));
    }

    private static ToolTraceEntry BuildTraceEntry(
        string callId,
        string? text,
        IReadOnlyDictionary<string, (string Name, string? Digest)> pending)
    {
        var name = pending.TryGetValue(callId, out var call) ? call.Name : "tool";
        var digest = pending.TryGetValue(callId, out var value) ? value.Digest : null;

        var (status, exitCode) = Classify(text);

        return new ToolTraceEntry(
            name,
            status,
            exitCode,
            digest,
            Summarize(text),
            text?.Length ?? 0,
            DateTime.Now,
            ParseEdits(text));
    }

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

        if (body.Contains("用户拒绝", StringComparison.Ordinal)
            || body.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return (ToolTraceEntry.StatusDenied, null);
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
