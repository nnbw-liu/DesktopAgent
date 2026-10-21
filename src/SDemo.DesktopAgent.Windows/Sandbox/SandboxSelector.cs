using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 按策略选择沙箱运行器（S0 骨架：只做选择与拒绝，不执行进程）。
/// 安全不变量：没有任何可用运行器时返回 <see cref="SandboxFailureCode.NotAvailable"/>，
/// 即使 <see cref="SandboxRequirement.Preferred"/> 也绝不回退到无沙箱执行。
/// </summary>
public sealed class SandboxSelector : ISandboxSelector
{
    private readonly IReadOnlyList<ISandboxRunner> _runners;

    public SandboxSelector(IEnumerable<ISandboxRunner> runners) => _runners = runners.ToArray();

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        ProbeAsync(SandboxKind.None, cancellationToken);

    public async Task<SandboxResult> RunAsync(
        SandboxRequest request,
        SandboxPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var availability = await ProbeAsync(policy.Preferred, cancellationToken).ConfigureAwait(false);
        if (!availability.Available || availability.Kind == SandboxKind.None)
        {
            return new SandboxResult
            {
                JobId = request.JobId,
                Kind = request.Kind,
                ExitCode = -1,
                Failure = SandboxFailureCode.NotAvailable,
                Message = availability.Reason
            };
        }

        // Required = 只用首选沙箱类型；首选不可用时拒绝，而不是悄悄换一个沙箱。
        // Preferred = 允许在沙箱类型之间降级；两者都不会降级到“无沙箱”。
        if (policy.Requirement == SandboxRequirement.Required
            && policy.Preferred != SandboxKind.None
            && availability.Kind != policy.Preferred)
        {
            return new SandboxResult
            {
                JobId = request.JobId,
                Kind = request.Kind,
                ExitCode = -1,
                Failure = SandboxFailureCode.NotAvailable,
                Message =
                    $"策略要求首选沙箱 {policy.Preferred}，但当前可用的是 {availability.Kind}；已拒绝降级执行。"
            };
        }

        var runner = _runners.First(r => r.Kind == availability.Kind);

        // 运行器选出来可能与调用方写的 Kind 不同（首选不可用时的降级），请求要跟着改。
        var effectiveRequest = request.Kind == runner.Kind ? request : request with { Kind = runner.Kind };
        return await runner.RunAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SandboxAvailability> ProbeAsync(SandboxKind preferred, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();

        foreach (var runner in _runners
                     .OrderBy(r => r.Kind == preferred ? 0 : 1)
                     .ThenBy(r => Rank(r.Kind)))
        {
            var availability = await runner.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (availability.Available)
            {
                return availability;
            }

            if (!string.IsNullOrWhiteSpace(availability.Reason))
            {
                reasons.Add($"{availability.Kind}：{availability.Reason}");
            }
        }

        return new SandboxAvailability(
            SandboxKind.None,
            Available: false,
            Verified: false,
            reasons.Count == 0 ? "没有可用的沙箱运行器。" : string.Join("；", reasons),
            Detail: null);
    }

    /// <summary>默认优先级：AppContainer（S1）→ Windows Sandbox（S4）→ 其他。</summary>
    private static int Rank(SandboxKind kind) => kind switch
    {
        SandboxKind.AppContainer => 0,
        SandboxKind.WindowsSandbox => 1,
        SandboxKind.JobObject => 2,
        SandboxKind.Remote => 3,
        _ => 4
    };
}
