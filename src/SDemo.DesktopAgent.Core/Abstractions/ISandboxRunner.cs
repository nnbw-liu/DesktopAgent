using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一种沙箱的具体执行器（AppContainer / Windows Sandbox …）。
/// 实现必须自己保证隔离；失败时返回带 <see cref="SandboxFailureCode"/> 的结果，不要抛异常给工具层。
/// </summary>
public interface ISandboxRunner
{
    SandboxKind Kind { get; }

    Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default);

    Task<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default);
}
