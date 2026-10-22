using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
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

    public AgentChatService(
        AgentToolRegistry tools,
        WorkspaceContext workspace,
        ISettingsService settings,
        IToolApprovalService approvals,
        ISandboxJobService sandbox,
        IToolAuditLog audit,
        IAppPaths paths,
        IFileReadRegistry reads)
    {
        _tools = tools;
        _workspace = workspace;
        _settings = settings;
        _approvals = approvals;
        _sandbox = sandbox;
        _audit = audit;
        _paths = paths;
        _reads = reads;
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
            plan.Session);

        var tools = _tools.Create(context, plan.ToolPacks, plan.Metrics);
        var agent = CreateAgent(plan.Provider, plan.ModelId, tools, plan.Context, telemetry);

        var pendingCalls = new Dictionary<string, (string Name, string? Digest)>(StringComparer.Ordinal);

        await foreach (var update in agent.RunStreamingAsync(plan.Context.Messages, cancellationToken: cancellationToken))
        {
            var text = new StringBuilder();
            var reasoning = new StringBuilder();

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case UsageContent { Details: { } usage }:
                        plan.Metrics?.AddUsage(usage);
                        break;
                    case TextReasoningContent { Text: { Length: > 0 } chain }:
                        reasoning.Append(chain);
                        break;
                    case TextContent { Text: { Length: > 0 } chunk } when content is not TextReasoningContent:
                        text.Append(chunk);
                        break;
                    case FunctionCallContent call:
                        pendingCalls[call.CallId] = (call.Name, DigestArguments(call.Arguments));
                        break;
                    case FunctionResultContent result:
                        plan.Metrics?.RecordToolTrace(BuildTraceEntry(result, pendingCalls));
                        break;
                }
            }

            if (text.Length > 0 || reasoning.Length > 0)
            {
                yield return new AgentDelta(text.ToString(), reasoning.ToString());
            }
        }
    }

    /// <summary>
    /// 把 provider 配置映射成 MAF 的 AIAgent。
    /// 中间插一层 <see cref="ContextBudgetChatClient"/>：FICC 在外层做工具循环，
    /// 每次 LLM 请求都会经过装饰器，因此能在循环内部裁剪（§3.7 分支 A）。
    /// </summary>
    private AIAgent CreateAgent(
        ModelProvider provider,
        string modelId,
        IReadOnlyList<AITool> tools,
        ContextPlan plan,
        ContextRunTelemetry telemetry)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl, UriKind.Absolute)
        };

        // 本地服务可能不需要 Key，但 OpenAI SDK 要求非空凭证。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(provider.ApiKey) ? "not-required" : provider.ApiKey);

        var chatClient = new OpenAIClient(credential, options).GetChatClient(modelId);

        var settings = _settings.Current.Context ?? new ContextSettings();
        var budgetOptions = new ContextBudgetChatOptions(
            Enabled: settings.Enabled && settings.MidTurnTrim,
            Budget: plan.Budget,
            Counter: new HeuristicTokenCounter(),
            CalibrationFactor: plan.Report.CalibrationFactor,
            KeepRecentTurns: settings.KeepRecentTurns,
            ToolResultChars: settings.MidTurnToolResultChars);

        // AsIChatClient() 是 OpenAI ChatClient → IChatClient 的桥；装饰器加在 AsAIAgent 之前，
        // 保证 FICC 在它外层（见 MidTurnPipelineSpikeTests）。
        var guarded = new ContextBudgetChatClient(chatClient.AsIChatClient(), budgetOptions, telemetry);

        return guarded.AsAIAgent(
            instructions: plan.Instructions,
            name: "SDemo Agent",
            tools: tools.Count == 0 ? null : tools.ToList());
    }

    private static ToolTraceEntry BuildTraceEntry(
        FunctionResultContent result,
        IReadOnlyDictionary<string, (string Name, string? Digest)> pending)
    {
        var name = pending.TryGetValue(result.CallId, out var call) ? call.Name : "tool";
        var digest = pending.TryGetValue(result.CallId, out var value) ? value.Digest : null;
        var text = result.Result?.ToString();

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
