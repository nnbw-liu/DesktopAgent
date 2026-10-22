namespace SDemo.DesktopAgent.Services;

/// <summary>一次清理 / 彻底清理的结果，设置页用它给出可解释的反馈。</summary>
public sealed record SandboxMaintenanceReport(
    int DeletedJobs,
    int PendingJobs,
    int DeletedTrashEntries,
    int DeletedTempFiles,
    IReadOnlyList<string> PendingJobIds)
{
    public static SandboxMaintenanceReport Empty { get; } = new(0, 0, 0, 0, []);

    /// <summary>仅“彻底清理”会设置：profile 是否删除成功；null 表示本次未尝试删除。</summary>
    public bool? ProfileDeleted { get; init; }
}

/// <summary>
/// 沙箱数据维护：启动时清过期 job、残留 tmp / wsb、过期 trash；
/// 设置页还可以“清理沙箱数据”（jobs / trash / 临时文件 + AppContainer profile）。
/// 有未应用变更的 job 必须跳过，不能把用户还没确认的 diff 直接删掉。
/// </summary>
public interface ISandboxMaintenance
{
    /// <summary>最近一次清理 / 彻底清理的结果；启动清理是后台任务，设置页只能读取最后结果。</summary>
    SandboxMaintenanceReport? LastReport { get; }

    Task<SandboxMaintenanceReport> CleanupAsync(CancellationToken cancellationToken = default);

    Task<SandboxMaintenanceReport> PurgeAsync(CancellationToken cancellationToken = default);
}

/// <summary>没有沙箱能力的平台（Android）不做事，但设置页仍然可以安全调用。</summary>
public sealed class UnavailableSandboxMaintenance : ISandboxMaintenance
{
    public SandboxMaintenanceReport? LastReport => null;

    public Task<SandboxMaintenanceReport> CleanupAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SandboxMaintenanceReport.Empty);

    public Task<SandboxMaintenanceReport> PurgeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SandboxMaintenanceReport.Empty);
}
