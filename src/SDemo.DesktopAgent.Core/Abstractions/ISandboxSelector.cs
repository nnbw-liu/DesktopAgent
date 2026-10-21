using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 按策略选中的运行器。任务需要先知道“会用哪种沙箱”才能准备不同的 job 目录 / 启动文件，
/// 所以把选择结果单独暴露出来，而不是只返回一个已经跑完的 <see cref="SandboxResult"/>。
/// </summary>
public sealed record SandboxSelection(SandboxAvailability Availability)
{
    /// <summary>选中且可用的运行器；不可用或没有运行器时为 null。</summary>
    public ISandboxRunner? Runner { get; init; }

    public bool IsAvailable => Runner is not null && Availability.Available;
}

/// <summary>
/// 按 <see cref="SandboxPolicy"/> 选择运行器。
/// 安全不变量：<see cref="SandboxRequirement.Required"/> 且没有可用运行器时必须返回
/// <see cref="SandboxFailureCode.NotAvailable"/>，禁止静默降级到无沙箱执行。
/// </summary>
public interface ISandboxSelector
{
    Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按策略挑选运行器，不执行。policy 为 Required 时只看首选类型；Preferred 时才允许换类型。
    /// <paramref name="requireVerified"/> 为 true 时，未通过自检（Verified=false）的候选不算可用，
    /// 这样 PowerShell 不会因为“能启动进程但跑不了脚本”的沙箱而放行。
    /// </summary>
    Task<SandboxSelection> SelectAsync(
        SandboxPolicy policy,
        bool requireVerified = false,
        CancellationToken cancellationToken = default);

    Task<SandboxResult> RunAsync(
        SandboxRequest request,
        SandboxPolicy policy,
        CancellationToken cancellationToken = default);
}
