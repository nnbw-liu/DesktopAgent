using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Services;

/// <summary>一次普通对话 / 任务轮次的输入。上下文取舍全部交给 <see cref="ContextBuilder"/>。</summary>
public sealed record AgentRunRequest(
    IReadOnlyList<ChatMessageModel> Messages,
    ModelProvider Provider,
    string ModelId,
    IReadOnlySet<string> ToolPacks,
    string? SkillName = null,
    string SessionId = "",
    AgentRunMetrics? Metrics = null);

/// <summary>
/// 应用层入口（§3.1）：负责「预算 → 裁剪 → 传输 → 校准 → 溢出重试」，
/// UI 只调用它，不再自己拼历史或决定发多少条消息。
/// </summary>
public interface IAgentSessionRunner
{
    /// <summary>只组装上下文、不发请求：用于输入框旁的占用预览。</summary>
    ContextPlan Preview(AgentRunRequest request);

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

    public AgentSessionRunner(
        IAgentChatService chat,
        ContextBuilder builder,
        ISettingsService settings,
        SkillCatalog? skills = null,
        TokenCalibrationStore? calibration = null)
    {
        _chat = chat;
        _builder = builder;
        _settings = settings;
        _skills = skills;
        _calibration = calibration;
    }

    public ContextPlan Preview(AgentRunRequest request) =>
        _builder.Build(BuildRequest(request, scale: 1.0));

    public async IAsyncEnumerable<AgentDelta> StreamAsync(
        AgentRunRequest request,
        ContextRunTelemetry telemetry,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current.Context ?? new ContextSettings();

        // 1. preflight：预算 + L1 裁剪；仍超限时直接抛出「需压缩」（P2 接摘要）。
        var plan = BuildOrReportFailure(request, scale: 1.0, telemetry);
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

        var retryPlan = BuildOrReportFailure(request, settings.OverflowRetryScale, telemetry);
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

    private ContextPlan BuildOrReportFailure(
        AgentRunRequest request,
        double scale,
        ContextRunTelemetry telemetry)
    {
        try
        {
            return _builder.Build(BuildRequest(request, scale));
        }
        catch (ContextBudgetException ex)
        {
            telemetry.Report = ex.Report;
            telemetry.ContextNotice = ex.Message;
            throw;
        }
    }

    private ContextBuildRequest BuildRequest(AgentRunRequest request, double scale) => new(
        request.Provider,
        request.ModelId,
        request.Messages,
        SkillName: request.SkillName,
        SkillText: _skills?.ReadContent(request.SkillName),
        PinnedMessageIds: request.Messages
            .Where(message => message.IsPinned)
            .Select(message => message.Id)
            .ToHashSet(StringComparer.Ordinal),
        BudgetScale: scale);

    private static AgentRunPlan ToPlan(AgentRunRequest request, ContextPlan context) => new(
        request.Provider,
        request.ModelId,
        context,
        request.ToolPacks,
        request.SessionId,
        request.Metrics);

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
