using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 不支持沙箱的平台（Android / 非 Windows 目标）使用的运行器。
/// 永远报告不可用，且拒绝一切请求——不存在“没有沙箱就直接跑”的回退路径。
/// </summary>
public sealed class UnavailableSandboxRunner : ISandboxRunner
{
    public const string Reason = "当前平台不支持 Windows 沙箱";

    public SandboxKind Kind => SandboxKind.None;

    public Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxAvailability(
            SandboxKind.None,
            Available: false,
            Verified: false,
            Reason,
            Detail: null));

    public Task<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SandboxResults.NotAvailable(request.JobId, Kind, Reason));
}
