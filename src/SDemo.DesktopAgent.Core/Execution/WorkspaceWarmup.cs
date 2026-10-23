namespace SDemo.DesktopAgent.Execution;

/// <summary>显式预 restore 的结果（用户点按钮触发，不产生聊天消息）。</summary>
public sealed record WorkspaceWarmupResult(bool Success, int ExitCode, string Message, long DurationMs);

/// <summary>预 restore：让构建所需的 obj/project.assets.json 先落在真实工作区。</summary>
public interface IWorkspaceWarmupService
{
    Task<WorkspaceWarmupResult> RestoreAsync(string workspaceRoot, CancellationToken cancellationToken = default);
}
