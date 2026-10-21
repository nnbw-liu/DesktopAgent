using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// Windows Sandbox 运行器（S0 占位，S4 实现）。
/// S4 只在 Pro / Enterprise / Education 上可用；Home 必须明确报告不可用，不能假装可用。
/// </summary>
public sealed class WindowsSandboxRunner : ISandboxRunner
{
    internal const string PendingReason = "S0 骨架：Windows Sandbox 运行器在 S4 实现。";

    public SandboxKind Kind => SandboxKind.WindowsSandbox;

    public Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxAvailability(
            Kind,
            Available: false,
            Verified: false,
            PendingReason,
            Detail: null));

    public Task<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxResult
        {
            JobId = request.JobId,
            Kind = Kind,
            ExitCode = -1,
            Failure = SandboxFailureCode.NotAvailable,
            Message = PendingReason
        });
}
