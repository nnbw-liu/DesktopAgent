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
        CancellationToken cancellationToken = default);

    Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default);
}
