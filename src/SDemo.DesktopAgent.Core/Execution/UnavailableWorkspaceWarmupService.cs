namespace SDemo.DesktopAgent.Execution;

/// <summary>非 Windows 宿主没有直跑/预 restore 能力；设置页仍可打开，点击时给出明确原因。</summary>
public sealed class UnavailableWorkspaceWarmupService : IWorkspaceWarmupService
{
    public Task<WorkspaceWarmupResult> RestoreAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkspaceWarmupResult(
            false,
            -1,
            "当前平台不支持预 restore（直跑只在 Windows 宿主可用）。",
            0));
}
