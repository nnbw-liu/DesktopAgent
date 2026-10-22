using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 按策略选择沙箱运行器。
/// 安全不变量：没有任何可用运行器时返回 <see cref="SandboxFailureCode.NotAvailable"/>，
/// 即使 <see cref="SandboxRequirement.Preferred"/> 也绝不回退到无沙箱执行。
/// </summary>
public sealed class SandboxSelector : ISandboxSelector
{
    private readonly IReadOnlyList<ISandboxRunner> _runners;

    public SandboxSelector(IEnumerable<ISandboxRunner> runners) => _runners = runners.ToArray();

    public async Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(
                new SandboxPolicy(SandboxRequirement.Preferred, SandboxKind.None),
                requireVerified: false,
                cancellationToken)
            .ConfigureAwait(false);

        return selection.Availability;
    }

    public async Task<SandboxSelection> SelectAsync(
        SandboxPolicy policy,
        bool requireVerified = false,
        CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();

        // Required = 只用首选沙箱类型；首选不可用时拒绝，而不是悄悄换一个沙箱。
        // Preferred = 允许在沙箱类型之间降级；两者都不会降级到“无沙箱”。
        var strict = policy.Requirement == SandboxRequirement.Required
                     && policy.Preferred != SandboxKind.None;

        foreach (var runner in Order(policy.Preferred, strict))
        {
            var availability = await runner.ProbeAsync(cancellationToken).ConfigureAwait(false);

            if (availability.Available && (!requireVerified || availability.Verified))
            {
                return new SandboxSelection(availability) { Runner = runner };
            }

            reasons.Add($"{availability.Kind}：" + (availability.Reason
                ?? (availability.Available ? "尚未通过自检" : "不可用")));
        }

        var reason = reasons.Count == 0
            ? "没有可用的沙箱运行器。"
            : string.Join("；", reasons);

        if (strict)
        {
            reason = $"策略要求首选沙箱 {policy.Preferred}，但当前不可用；已拒绝降级执行。{reason}";
        }

        return new SandboxSelection(new SandboxAvailability(
            SandboxKind.None,
            Available: false,
            Verified: false,
            Reason: reason,
            Detail: null));
    }

    public async Task<SandboxResult> RunAsync(
        SandboxRequest request,
        SandboxPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(policy, requireVerified: false, cancellationToken)
            .ConfigureAwait(false);

        if (!selection.IsAvailable)
        {
            return new SandboxResult
            {
                JobId = request.JobId,
                Kind = request.Kind,
                ExitCode = -1,
                Failure = SandboxFailureCode.NotAvailable,
                Message = selection.Availability.Reason
            };
        }

        var runner = selection.Runner!;

        // 运行器选出来可能与调用方写的 Kind 不同（首选不可用时的降级），请求要跟着改。
        var effectiveRequest = request.Kind == runner.Kind ? request : request with { Kind = runner.Kind };
        return await runner.RunAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<ISandboxRunner> Order(SandboxKind preferred, bool strict)
    {
        var ordered = _runners
            .OrderBy(runner => runner.Kind == preferred ? 0 : 1)
            .ThenBy(runner => Rank(runner.Kind));

        return strict ? ordered.Where(runner => runner.Kind == preferred) : ordered;
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
