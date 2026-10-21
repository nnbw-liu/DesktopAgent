using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 按 <see cref="SandboxPolicy"/> 选择运行器。
/// 安全不变量：<see cref="SandboxRequirement.Required"/> 且没有可用运行器时必须返回
/// <see cref="SandboxFailureCode.NotAvailable"/>，禁止静默降级到无沙箱执行。
/// </summary>
public interface ISandboxSelector
{
    Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<SandboxResult> RunAsync(
        SandboxRequest request,
        SandboxPolicy policy,
        CancellationToken cancellationToken = default);
}
