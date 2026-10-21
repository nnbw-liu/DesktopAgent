using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// AppContainer 运行器（S0 占位）。
/// S1 实现：profile SID、Job Object、最小 ACL、CreateProcess(CREATE_SUSPENDED) + ResumeThread、超时终止。
/// 在此之前一律报告不可用，绝不启动任何进程。
/// </summary>
public sealed class AppContainerSandboxRunner : ISandboxRunner
{
    internal const string PendingReason = "S0 骨架：AppContainer 运行器在 S1 实现。";

    public SandboxKind Kind => SandboxKind.AppContainer;

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
