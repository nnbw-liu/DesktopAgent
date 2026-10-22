using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>测试用沙箱作业服务：记录调用参数，返回预先设置的 PDF 结果。</summary>
internal sealed class FakeSandboxJobService : ISandboxJobService
{
    public sealed record PdfCall(string Path, string? Pages, int MaxChars);

    public List<PdfCall> PdfCalls { get; } = [];

    public PdfExtraction Result { get; set; } = new(
        Ok: true,
        PageCount: 0,
        Encrypted: false,
        HasTextLayer: false,
        Pages: [],
        Truncated: false,
        Message: null);

    public SandboxAvailability Availability { get; set; } = new(
        SandboxKind.AppContainer,
        Available: true,
        Verified: false,
        Reason: null,
        Detail: null);

#pragma warning disable CS0067
    public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;
#pragma warning restore CS0067

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Availability);

    public Task<SandboxAvailability> GetAvailabilityAsync(
        SandboxKind kind,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Availability);

    public Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("测试不应调用 RunPowerShellAsync。");

    public Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        PdfCalls.Add(new PdfCall(path, pages, maxChars));
        return Task.FromResult(Result);
    }

    public Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxProbeReport(true, true, true, true, true, true, true, null));

    public Task<WindowsSandboxSelfTestReport> RunWindowsSandboxSelfTestAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowsSandboxSelfTestReport(false, false, false, TimeSpan.Zero, "未启用"));
}
