using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱作业服务（S0 占位）。这里已经实现了“先问可用性、不可用就拒绝”的前置判断；
/// 真正的 job 目录、工作区副本、SandboxHost 启动与结果解析在 S2 实现。
/// </summary>
public sealed class SandboxJobService : ISandboxJobService
{
#pragma warning disable CS0067 // S0 没有真正执行的作业，S2 完成后才会触发。
    public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;
#pragma warning restore CS0067

    private readonly ISandboxSelector _selector;

    public SandboxJobService(ISandboxSelector selector) => _selector = selector;

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        _selector.GetAvailabilityAsync(cancellationToken);

    public async Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var availability = await _selector.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        // PowerShell 必须 Available && Verified：cmd 能跑不代表 PowerShell 能跑。
        if (!availability.Available || !availability.Verified)
        {
            return Unavailable(sessionId, availability);
        }

        // S2 实现：创建 job 目录 → 写 script.ps1 / job.json → 启动 SandboxHost → 读 result.json → 计算变更集。
        throw new NotSupportedException("沙箱作业执行在 S2 阶段实现。");
    }

    public async Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        var availability = await _selector.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        var reason = availability.Reason ?? "沙箱不可用。";

        if (!availability.Available || !availability.Verified)
        {
            return new PdfExtraction(
                Ok: false,
                PageCount: 0,
                Encrypted: false,
                HasTextLayer: false,
                Pages: [],
                Truncated: false,
                Message: reason);
        }

        // S5 实现：把 PDF 复制到 input、由 SandboxHost 解析、读回 pdf.json。
        throw new NotSupportedException("沙箱 PDF 解析在 S5 阶段实现。");
    }

    private static SandboxJobOutcome Unavailable(string sessionId, SandboxAvailability availability)
    {
        var result = new SandboxResult
        {
            JobId = string.Empty,
            Kind = availability.Kind,
            ExitCode = -1,
            Failure = SandboxFailureCode.NotAvailable,
            Message = availability.Reason ?? "沙箱不可用。"
        };

        return new SandboxJobOutcome(sessionId, string.Empty, result, [], string.Empty);
    }
}
