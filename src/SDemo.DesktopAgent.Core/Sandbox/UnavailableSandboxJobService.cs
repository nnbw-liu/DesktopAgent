using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 不支持沙箱的平台使用的作业服务：可用性固定为不可用，
/// PowerShell 与 PDF 都直接返回 NotAvailable，不创建 job 目录、不启动任何进程。
/// </summary>
public sealed class UnavailableSandboxJobService : ISandboxJobService
{
#pragma warning disable CS0067 // 没有可用沙箱就不会产生 outcome。
    public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;
#pragma warning restore CS0067

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxAvailability(
            SandboxKind.None,
            Available: false,
            Verified: false,
            UnavailableSandboxRunner.Reason,
            Detail: null));

    public Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        var reason = UnavailableSandboxRunner.Reason;
        return Task.FromResult(new SandboxJobOutcome(
            sessionId,
            string.Empty,
            SandboxResults.NotAvailable(string.Empty, SandboxKind.None, reason),
            [],
            string.Empty));
    }

    public Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PdfExtraction(
            Ok: false,
            PageCount: 0,
            Encrypted: false,
            HasTextLayer: false,
            Pages: [],
            Truncated: false,
            Message: UnavailableSandboxRunner.Reason));

    public Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxProbeReport(
            CmdOk: false,
            PowerShellOk: false,
            CannotReadUserProfile: false,
            CannotWriteWorkspace: false,
            NoNetwork: false,
            SubprocessOk: false,
            TimeoutOk: false,
            Detail: UnavailableSandboxRunner.Reason));
}
