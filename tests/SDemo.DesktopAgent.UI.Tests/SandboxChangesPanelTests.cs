using System.IO;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 沙箱结果面板：标出副作用类别、系统级脚本给出警告，并且**写回真实工作区前先打检查点**。
/// </summary>
public sealed class SandboxChangesPanelTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSandboxJobService _sandbox = new();
    private readonly CheckpointStore _checkpoints;

    public SandboxChangesPanelTests()
    {
        _checkpoints = new CheckpointStore(Path.Combine(_temp.Root, "checkpoints"), new NoAudit());

        Services.AddSingleton<ISandboxJobService>(_sandbox);
        Services.AddSingleton(new ChangeApplier());
        Services.AddSingleton(_checkpoints);
    }

    [Fact]
    public void 系统级脚本会标红并说明不会重放()
    {
        var outcome = BuildOutcome("shutdown /s /t 3600", workspaceContent: "old", workContent: "new");

        var cut = RenderComponent<SandboxChangesPanel>(parameters => parameters
            .Add(component => component.SessionId, "s1"));

        cut.InvokeAsync(() => _sandbox.Raise(outcome));
        cut.WaitForAssertion(() => Assert.Contains("系统级操作", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("不会", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("shutdown", cut.Find(".sandbox-panel-warn").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 应用变更前会生成检查点()
    {
        var outcome = BuildOutcome("'new' | Set-Content a.txt", workspaceContent: "old", workContent: "new");
        var workspace = outcome.WorkspaceRoot;
        var target = Path.Combine(workspace, "a.txt");

        var cut = RenderComponent<SandboxChangesPanel>(parameters => parameters
            .Add(component => component.SessionId, "s1"));

        cut.InvokeAsync(() => _sandbox.Raise(outcome));
        cut.WaitForAssertion(() => Assert.Contains("应用选中", cut.Markup, StringComparison.Ordinal));

        cut.Find(".primary-button").Click();
        cut.FindAll(".modal .primary-button").First().Click();

        cut.WaitForAssertion(() => Assert.Equal("new", File.ReadAllText(target)));

        // 检查点先于写入：撤销时能拿回旧内容。
        var checkpoint = Assert.Single(_checkpoints.List("s1"));
        Assert.Contains(checkpoint.Files, file => file.Path == "a.txt" && file.Existed);
    }

    [Fact]
    public void 联网脚本给出提示但不阻止应用()
    {
        var outcome = BuildOutcome("Invoke-WebRequest https://example.com -OutFile a.txt", "old", "new");

        var cut = RenderComponent<SandboxChangesPanel>(parameters => parameters
            .Add(component => component.SessionId, "s1"));

        cut.InvokeAsync(() => _sandbox.Raise(outcome));
        cut.WaitForAssertion(() => Assert.Contains("联网", cut.Markup, StringComparison.Ordinal));

        Assert.Empty(cut.FindAll(".sandbox-panel-warn"));
    }

    /// <summary>造一个可应用的真实 job：manifest + work 副本 + 一条 Modified 变更。</summary>
    private SandboxJobOutcome BuildOutcome(string script, string workspaceContent, string workContent)
    {
        var workspace = Path.Combine(_temp.Root, "workspace");
        Directory.CreateDirectory(workspace);

        var target = Path.Combine(workspace, "a.txt");
        File.WriteAllText(target, workspaceContent);
        var info = new FileInfo(target);

        var jobDir = Path.Combine(_temp.Root, "job-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(jobDir, "work"));
        File.WriteAllText(Path.Combine(jobDir, "work", "a.txt"), workContent);

        new WorkspaceManifest
        {
            Entries =
            [
                new WorkspaceManifestEntry("a.txt", info.Length, info.LastWriteTimeUtc, Sha256: null)
            ]
        }.Save(Path.Combine(jobDir, "manifest.json"));

        var changes = new[]
        {
            new SandboxChange("a.txt", SandboxChangeKind.Modified, workContent.Length, false, "@@ -1 +1 @@")
        };

        return new SandboxJobOutcome(
            "s1",
            "job-1",
            new SandboxResult { JobId = "job-1", Kind = SandboxKind.AppContainer, ExitCode = 0 },
            changes,
            jobDir)
        {
            WorkspaceRoot = workspace,
            Effects = SandboxEffectDetector.Detect(script, changes)
        };
    }

    private sealed class FakeSandboxJobService : ISandboxJobService
    {
        public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;

        public void Raise(SandboxJobOutcome outcome) => OutcomeAvailable?.Invoke(this, outcome);

        public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxAvailability(SandboxKind.AppContainer, true, true, null, null));

        public Task<SandboxAvailability> GetAvailabilityAsync(
            SandboxKind kind,
            CancellationToken cancellationToken = default) => GetAvailabilityAsync(cancellationToken);

        public Task<SandboxJobOutcome> RunPowerShellAsync(
            string sessionId,
            string script,
            string? workingDirectory,
            int timeoutSeconds,
            string? workspaceRoot = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试直接 Raise 结果");

        public Task<PdfExtraction> ExtractPdfAsync(
            string path,
            string? pages,
            int maxChars,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不解析 PDF");

        public Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不自检");

        public Task<WindowsSandboxSelfTestReport> RunWindowsSandboxSelfTestAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不自检");
    }
}
