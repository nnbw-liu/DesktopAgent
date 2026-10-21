namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱清理器（S0 占位，S6 实现）：清理过期 job 目录、残留进程与临时 ACL。
/// 占位实现不做任何事，保证应用启动 / 退出路径可以先接上。
/// </summary>
public sealed class SandboxJanitor
{
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
