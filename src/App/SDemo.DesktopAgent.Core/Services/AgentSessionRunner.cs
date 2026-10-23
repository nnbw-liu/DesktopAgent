using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatSessionModel = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.DesktopAgent.Services;

/// <summary>一次普通对话 / 任务轮次的输入。上下文取舍全部交给 <see cref="ContextBuilder"/>。</summary>
public sealed record AgentRunRequest(
    IReadOnlyList<ChatMessageModel> Messages,
    ModelProvider Provider,
    string ModelId,
    IReadOnlySet<string> ToolPacks,
    string? SkillName = null,
    string SessionId = "",
    AgentRunMetrics? Metrics = null,
    ChatSessionModel? Session = null);

/// <summary>手动「压缩现在」的输入。</summary>
public sealed record AgentCompactRequest(
    ChatSessionModel Session,
    ModelProvider Provider,
    string ModelId,
    string SessionId = "");

/// <summary>
/// 应用层入口（§3.1）：负责「预算 → 压缩 / 裁剪 → 传输 → 校准 → 溢出重试」，
/// UI 只调用它，不再自己拼历史或决定发多少条消息。
/// </summary>
public interface IAgentSessionRunner
{
    /// <summary>只组装上下文、不发请求：用于输入框旁的占用预览。</summary>
    ContextPlan Preview(AgentRunRequest request);

    /// <summary>手动触发一次增量压缩（会话菜单「压缩现在」）。</summary>
    Task<CompactionOutcome> CompactAsync(
        AgentCompactRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>流式执行；<paramref name="telemetry"/> 由调用方创建，流结束后读取报告。</summary>
    IAsyncEnumerable<AgentDelta> StreamAsync(
        AgentRunRequest request,
        ContextRunTelemetry telemetry,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentSessionRunner : IAgentSessionRunner
{
    private readonly IAgentChatService _chat;
    private readonly ContextBuilder _builder;
    private readonly SkillCatalog? _skills;
    private readonly TokenCalibrationStore? _calibration;
    private readonly ISettingsService _settings;
    private readonly ContextCompactor? _compactor;
    private readonly WorkspaceStateProvider? _statePack;
    private readonly WorkspaceContext? _workspace;
    private readonly AgentToolRegistry? _tools;

    public AgentSessionRunner(
        IAgentChatService chat,
        ContextBuilder builder,
        ISettingsService settings,
        SkillCatalog? skills = null,
        TokenCalibrationStore? calibration = null,
        ContextCompactor? compactor = null,
        WorkspaceStateProvider? statePack = null,
        WorkspaceContext? workspace = null,
        AgentToolRegistry? tools = null)
    {
        _chat = chat;
        _builder = builder;
        _settings = settings;
        _skills = skills;
        _calibration = calibration;
        _compactor = compactor;
        _statePack = statePack;
        _workspace = workspace;
        _tools = tools;
    }

    public ContextPlan Preview(AgentRunRequest request) =>
        _builder.Build(BuildRequest(request, scale: 1.0, BuildStatePackText(request)));

    public async Task<CompactionOutcome> CompactAsync(
        AgentCompactRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_compactor is null)
        {
            return CompactionOutcome.Failure("摘要压缩未启用。");
        }

        var settings = _settings.Current.Context ?? new ContextSettings();
        var profile = _builder.ResolveProfile(new ContextBuildRequest(
            request.Provider,
            request.ModelId,
            request.Session.Messages.ToList()));
        var budget = ContextBudget.Create(profile, settings);
        var (summaryProvider, summaryModel) = ResolveSummaryTarget(
            request.Provider, request.ModelId, settings);

        return await _compactor.EnsureAsync(
            new ContextCompactionRequest(
                request.Session,
                summaryProvider,
                summaryModel,
                profile,
                settings,
                budget,
                Force: true),
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentDelta> StreamAsync(
        AgentRunRequest request,
        ContextRunTelemetry telemetry,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current.Context ?? new ContextSettings();
        var statePackText = BuildStatePackText(request);

        // 1. preflight：预算 + 压缩 / L1 裁剪；摘要失败或仍超限时抛出「需压缩」。
        var plan = await BuildOrCompactAsync(request, scale: 1.0, telemetry, statePackText, cancellationToken)
            .ConfigureAwait(false);
        telemetry.Plan = plan;
        telemetry.Report = plan.Report;

        var yieldedAny = false;
        Exception? failure = null;

        // 用显式枚举器：C# 不允许在带 catch 的 try 里 yield，这里把「取下一项的异常」
        // 单独捕获，yield 只出现在 finally-only 的 try 里。
        var enumerator = _chat.RunAsync(ToPlan(request, plan), telemetry, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool moved;

                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                    break;
                }

                if (!moved)
                {
                    break;
                }

                yieldedAny = true;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        // 2. 已经吐出正文后不再重试：重试会把内容重复一遍，成本也翻倍。
        if (failure is null)
        {
            CompleteRun(request, plan, telemetry);
            yield break;
        }

        ContextOverflowInfo? overflow = null;

        if (!yieldedAny
            && settings.OverflowRetry
            && ContextOverflowDetector.TryDetect(failure, out var detected))
        {
            overflow = detected;
        }

        if (overflow is null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // 3. 溢出自愈：学习窗口 → 半预算重算 → 只重试一次（§3.8）。
        telemetry.OverflowRetried = true;

        if (overflow.LearnedWindow is int learned)
        {
            _calibration?.LearnWindow(request.Provider.Id, request.ModelId, learned);
            telemetry.ContextNotice =
                $"服务端提示上下文窗口为 {FormatTokens(learned)}，已记录；本轮按半预算重试一次。";
        }
        else
        {
            telemetry.ContextNotice = "上下文超限，已按半预算重试一次。";
        }

        var retryPlan = await BuildOrCompactAsync(
                request, settings.OverflowRetryScale, telemetry, statePackText, cancellationToken)
            .ConfigureAwait(false);
        telemetry.Plan = retryPlan;

        // 第一次尝试的估算不算进本次校准：那次请求没有成功，usage 也不完整。
        telemetry.EstimatedInputTokens = 0;
        telemetry.Report = retryPlan.Report with
        {
            OverflowRetried = true,
            LearnedWindow = overflow.LearnedWindow
        };

        Exception? retryFailure = null;
        var retryEnumerator = _chat.RunAsync(ToPlan(request, retryPlan), telemetry, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool moved;

                try
                {
                    moved = await retryEnumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    retryFailure = ex;
                    break;
                }

                if (!moved)
                {
                    break;
                }

                yield return retryEnumerator.Current;
            }
        }
        finally
        {
            await retryEnumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (retryFailure is not null)
        {
            if (ContextOverflowDetector.TryDetect(retryFailure, out _))
            {
                var report = telemetry.Report;
                throw new ContextBudgetException(
                    AgentErrorKind.ContextOverflow,
                    $"上下文超限：已用约 {FormatTokens(report.Estimated)} token / 预算 {FormatTokens(report.Total)}，"
                    + $"窗口 {FormatTokens(report.ContextWindow)}。已按半预算重试一次仍失败；"
                    + "请新建会话、减少附件，或在设置 → 模型里确认窗口值。",
                    report);
            }

            ExceptionDispatchInfo.Capture(retryFailure).Throw();
        }

        CompleteRun(request, retryPlan, telemetry);
    }

    /// <summary>
    /// 组装上下文。P2 的触发条件是「L1 已经需要淘汰历史」：此时先把被淘汰的一段压成摘要，
    /// 再带摘要重建（被淘汰的消息不再丢失，而是变成摘要）。压缩失败 / 没有可压缩内容时，
    /// 继续使用 P1 的硬裁剪结果，不阻塞请求。
    /// </summary>
    private async Task<ContextPlan> BuildOrCompactAsync(
        AgentRunRequest request,
        double scale,
        ContextRunTelemetry telemetry,
        string? statePackText,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current.Context ?? new ContextSettings();
        var buildRequest = BuildRequest(request, scale, statePackText);

        ContextPlan plan;

        try
        {
            plan = _builder.Build(buildRequest);
        }
        catch (ContextBudgetException ex)
        {
            telemetry.Report = ex.Report;

            if (ex.Kind != AgentErrorKind.NeedsCompaction || !CanCompact(request, settings))
            {
                telemetry.ContextNotice = ex.Message;
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw; // 不可达；让编译器看到方法总有返回值。
            }

            // 连 L1 都放不下：必须压缩；压缩不出来就保持 P1 的报错。
            return await CompactAndRebuildAsync(
                request, scale, telemetry, statePackText, ex, cancellationToken).ConfigureAwait(false);
        }

        // 没有淘汰任何历史 = 没超预算：一次摘要调用都不发生（P2 DoD）。
        if (plan.Report.OmittedMessages == 0 || !CanCompact(request, settings))
        {
            return plan;
        }

        var outcome = await CompactAsync(
            request, scale, telemetry, cancellationToken).ConfigureAwait(false);

        if (!outcome.Compacted)
        {
            // 摘要不可用 / 没有可压缩内容：保持 L1 结果，继续回答。
            return plan;
        }

        try
        {
            var rebuilt = _builder.Build(BuildRequest(request, scale, statePackText));
            telemetry.Report = rebuilt.Report;
            return rebuilt;
        }
        catch (ContextBudgetException afterCompact)
        {
            // 近期轮次本身太大，摘要帮不上忙：这一轮仍按 L1 结果发送（摘要留给下一轮）。
            telemetry.Report = plan.Report;
            telemetry.ContextNotice = afterCompact.Message;
            return plan;
        }
    }

    /// <summary>L1 放不下时：压缩 → 重建；失败则原样抛出 <paramref name="original"/>。</summary>
    private async Task<ContextPlan> CompactAndRebuildAsync(
        AgentRunRequest request,
        double scale,
        ContextRunTelemetry telemetry,
        string? statePackText,
        ContextBudgetException original,
        CancellationToken cancellationToken)
    {
        var outcome = await CompactAsync(
            request, scale, telemetry, cancellationToken).ConfigureAwait(false);

        if (!outcome.Compacted)
        {
            throw original;
        }

        try
        {
            var rebuilt = _builder.Build(BuildRequest(request, scale, statePackText));
            telemetry.Report = rebuilt.Report;
            return rebuilt;
        }
        catch (ContextBudgetException again)
        {
            telemetry.Report = again.Report;
            throw;
        }
    }

    private bool CanCompact(AgentRunRequest request, ContextSettings settings) =>
        settings.AutoCompact && request.Session is not null && _compactor is not null;

    /// <summary>解析摘要模型并跑一次增量压缩；异常不外抛（自动压缩路径必须不阻塞）。</summary>
    private async Task<CompactionOutcome> CompactAsync(
        AgentRunRequest request,
        double scale,
        ContextRunTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        if (request.Session is null || _compactor is null)
        {
            return CompactionOutcome.NotNeeded("未启用压缩。");
        }

        var settings = _settings.Current.Context ?? new ContextSettings();
        var buildRequest = BuildRequest(request, scale, statePackText: null);
        var profile = _builder.ResolveProfile(buildRequest);

        var budget = new ContextPolicy(profile, settings).Budget;
        if (Math.Abs(scale - 1.0) > 1e-9 && scale > 0)
        {
            budget = budget.Scale(scale);
        }

        var (summaryProvider, summaryModel) = ResolveSummaryTarget(
            request.Provider, request.ModelId, settings);

        CompactionOutcome outcome;

        try
        {
            outcome = await _compactor.EnsureAsync(
                new ContextCompactionRequest(
                    request.Session,
                    summaryProvider,
                    summaryModel,
                    profile,
                    settings,
                    budget),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CompactionOutcome.Failure($"压缩失败，继续使用硬裁剪：{ex.Message}", ex.Message);
        }

        // 只有真正压缩 / 压缩失败才给用户提示；「没有可压缩的新增历史」属于正常状态。
        if (outcome.Compacted || outcome.Failed)
        {
            telemetry.ContextNotice = outcome.Message;
        }

        return outcome;
    }

    private ContextBuildRequest BuildRequest(
        AgentRunRequest request,
        double scale,
        string? statePackText)
    {
        var build = new ContextBuildRequest(
            request.Provider,
            request.ModelId,
            request.Messages,
            SkillName: request.SkillName,
            SkillText: _skills?.ReadContent(request.SkillName),
            PinnedMessageIds: request.Messages
                .Where(message => message.IsPinned)
                .Select(message => message.Id)
                .ToHashSet(StringComparer.Ordinal),
            BudgetScale: scale,
            SummaryText: request.Session?.Summary,
            SummaryThroughOrder: request.Session?.SummaryThroughOrder ?? 0,
            StatePackText: statePackText,
            SummaryFallback: request.Session?.SummaryIsFallback ?? false);

        return build with { Routing = RouteCapabilities(request, build) };
    }

    /// <summary>
    /// M1：每轮在这里路由一次（规则版），Preview 与实际请求走同一套判定，
    /// 因此占用环里看到的工具段和被发送的工具 schema 是同一个决策。
    /// 插件 Run / 未注册工具时返回 null，保持旧行为。
    /// </summary>
    private RoutingDecision? RouteCapabilities(AgentRunRequest request, ContextBuildRequest build)
    {
        if (_tools is null)
        {
            return null;
        }

        var available = _tools.ToolNames(request.ToolPacks);
        if (available.Count == 0)
        {
            return RoutingDecision.Empty;
        }

        var profile = _builder.ResolveProfile(build);
        var agent = _settings.Current.Agent ?? new AgentSettings();
        var mode = AgentModeResolver.Resolve(
            profile,
            request.ModelId,
            agent.Mode);

        var userInput = request.Messages
            .LastOrDefault(message => message.Role == ChatMessageRole.User)?.Content ?? string.Empty;
        var recent = request.Metrics?.ToolTrace
            .Select(entry => entry.ToolId)
            .TakeLast(6)
            .ToArray() ?? [];

        return CapabilityRouter.Route(new CapabilityRoutingInput(
            userInput,
            recent,
            available,
            mode,
            agent.ResolveMaxExposedTools(profile.ContextWindow, mode),
            agent.SearchCapabilitiesEnabled,
            agent.ExposeAllTools));
    }

    /// <summary>
    /// 工作区状态包：树（30s 缓存）+ 最近改动 + 最近动作。没有工作区 / 关闭时返回 null，
    /// 不阻塞请求。
    /// </summary>
    private string? BuildStatePackText(AgentRunRequest request)
    {
        if (_statePack is null || _workspace is null || request.Session is null)
        {
            return null;
        }

        var settings = _settings.Current.Context ?? new ContextSettings();
        if (!settings.StatePackEnabled)
        {
            return null;
        }

        var root = _workspace.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            var profile = _builder.ResolveProfile(BuildRequest(request, 1.0, statePackText: null));
            var cap = profile.IsSmallModel
                ? settings.StatePackSmallModelTokenBudget
                : settings.StatePackTokenBudget;

            var state = _statePack.Build(root, request.Session);
            return StatePackFormatter.Format(state, cap, _builder.Counter).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 状态包是增强信息：读不到工作区时不影响本轮请求。
            return null;
        }
    }

    /// <summary>摘要模型：显式配置优先，配置失效时回退当前会话模型（P2 §3.5）。</summary>
    private (ModelProvider Provider, string ModelId) ResolveSummaryTarget(
        ModelProvider sessionProvider,
        string sessionModelId,
        ContextSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SummaryProviderId)
            || string.IsNullOrWhiteSpace(settings.SummaryModelId))
        {
            return (sessionProvider, sessionModelId);
        }

        var provider = _settings.Current.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, settings.SummaryProviderId, StringComparison.Ordinal));

        if (provider is null
            || !provider.Models.Any(model => string.Equals(model.Id, settings.SummaryModelId, StringComparison.Ordinal)))
        {
            return (sessionProvider, sessionModelId);
        }

        return (provider, settings.SummaryModelId);
    }

    private static AgentRunPlan ToPlan(AgentRunRequest request, ContextPlan context) => new(
        request.Provider,
        request.ModelId,
        context,
        request.ToolPacks,
        request.SessionId,
        request.Metrics,
        request.Session);

    /// <summary>成功收尾：写回实际 usage 并做校准（服务端不返回 usage 时自动跳过）。</summary>
    private void CompleteRun(AgentRunRequest request, ContextPlan plan, ContextRunTelemetry telemetry)
    {
        var actual = request.Metrics?.InputTokens ?? 0;
        var estimated = telemetry.EstimatedInputTokens > 0
            ? telemetry.EstimatedInputTokens
            : plan.Report.Estimated;

        if (actual > 0)
        {
            _calibration?.RecordUsage(request.Provider.Id, request.ModelId, estimated, actual);

            // 校准后的报告把实际值带给 UI，便于核对「估算 vs usage」。
            telemetry.Report = (telemetry.Report ?? plan.Report) with { ActualInputTokens = actual };
        }
    }

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();
}
