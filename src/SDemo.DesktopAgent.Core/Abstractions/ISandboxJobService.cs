using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 工具层的沙箱入口：负责 job 目录、工作区副本、结果解析与变更集（S2 实现）。
/// S0 只定义接口；没有沙箱实现时返回 NotAvailable，不做任何进程执行。
/// </summary>
public interface ISandboxJobService
{
    event EventHandler<SandboxJobOutcome>? OutcomeAvailable;

    Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default);

    Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 运行设置页的“自检”：基本进程 / PowerShell / 读用户目录 / 写工作区 / 联网 /
    /// 子进程 / 超时终止。Noop 实现返回全 false 并给出原因，不抛异常。
    /// </summary>
    Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default);
}
