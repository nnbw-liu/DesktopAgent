using System.IO;
using System.Text;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Tools;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S3 PowerShell 工具包：没有沙箱 / 未启用 / 脚本超限 / 目录逃逸都要在
/// 审批与执行之前拒绝；批准后才调用沙箱，结果格式化并写审计。
/// </summary>
public sealed class PowerShellToolPackTests
{
    [Fact]
    public void 工具包是内置能力且默认开启()
    {
        var descriptor = new PowerShellToolPack(new RecordingDirectRunner()).Descriptor;

        Assert.True(descriptor.BuiltIn);
        Assert.True(descriptor.DefaultEnabled);
        Assert.True(descriptor.WindowsOnly);
    }

    [Fact]
    public async Task 工具包未启用时直接拒绝()
    {
        var harness = new Harness();
        // 默认已改为开启；这里显式关闭，覆盖“用户全局关掉”的分支。
        harness.Settings.Current.Tools.SetPackEnabled(PowerShellToolPack.PackId, false);

        var text = await harness.RunAsync("Write-Output ok");

        Assert.Contains("未启用", text);
        Assert.Empty(harness.Jobs.Calls);
        Assert.Null(harness.Approvals.Request);
    }

    [Fact]
    public async Task 脚本超过64KB时拒绝()
    {
        var harness = new Harness();
        harness.Enable();

        var text = await harness.RunAsync(new string('a', PowerShellToolPack.MaxScriptBytes + 1));

        Assert.Contains("超过 64KB", text);
        Assert.Empty(harness.Jobs.Calls);
        Assert.Null(harness.Approvals.Request);
    }

    [Fact]
    public async Task 沙箱不可用时拒绝且不请求审批()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Jobs.Availability = new SandboxAvailability(
            SandboxKind.None,
            Available: false,
            Verified: false,
            "AppContainer 不可用。",
            Detail: null);

        var text = await harness.RunAsync("Write-Output ok");

        Assert.StartsWith("ERROR: 沙箱不可用", text);
        Assert.Contains("AppContainer 不可用", text);
        Assert.Null(harness.Approvals.Request);
        Assert.Empty(harness.Jobs.Calls);
    }

    [Fact]
    public async Task 沙箱可见但未验证时拒绝()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Jobs.Availability = new SandboxAvailability(
            SandboxKind.AppContainer,
            Available: true,
            Verified: false,
            "PowerShell 自检未通过。",
            Detail: null);

        var text = await harness.RunAsync("Write-Output ok");

        Assert.StartsWith("ERROR: 沙箱不可用", text);
        Assert.Null(harness.Approvals.Request);
        Assert.Empty(harness.Jobs.Calls);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\Windows")]
    [InlineData("\\\\server\\share")]
    public async Task 工作目录逃逸时拒绝(string directory)
    {
        var harness = new Harness();
        harness.Enable();

        var text = await harness.RunAsync("Write-Output ok", directory);

        Assert.Contains("工作目录无效", text);
        Assert.Null(harness.Approvals.Request);
        Assert.Empty(harness.Jobs.Calls);
    }

    [Fact]
    public async Task 拒绝审批后不执行且审计记录拒绝()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Approvals.Decision = ToolApprovalDecision.Deny;

        var text = await harness.RunAsync("Write-Output ok");

        Assert.Contains("拒绝了", text);
        Assert.Empty(harness.Jobs.Calls);
        var audit = Assert.Single(harness.Audit.Entries);
        Assert.Equal("Deny", audit.Approval);
        Assert.StartsWith("sha256:", audit.ScriptSha256);
        Assert.Null(audit.ScriptPreview);
    }

    [Fact]
    public async Task 审批请求展示完整脚本与工作目录()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Approvals.Decision = ToolApprovalDecision.Deny;
        const string script = "Get-ChildItem -Path .\nWrite-Output '中文'";

        await harness.RunAsync(script, "sub");

        var request = Assert.IsType<ToolApprovalRequest>(harness.Approvals.Request);
        Assert.Equal(ToolApprovalKind.ProcessExecution, request.Kind);
        Assert.Equal(script, request.Detail);
        Assert.Contains("AppContainer", request.Summary);
        Assert.Contains("目录：./sub", request.Summary);
        Assert.Contains("超时：30s", request.Summary);
        Assert.False(request.AllowSessionRemember);
    }

    [Fact]
    public async Task 批准后执行并格式化变更且写审计()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Jobs.Outcome = Outcome(
            new SandboxResult
            {
                JobId = "job-1",
                Kind = SandboxKind.AppContainer,
                ExitCode = 0,
                StdOut = "done\n",
                Duration = TimeSpan.FromMilliseconds(812)
            },
            new SandboxChange("out/report.md", SandboxChangeKind.Added, 12, false, null));

        var text = await harness.RunAsync("New-Item -ItemType File out/report.md", "sub");

        Assert.Contains("退出码：0", text);
        Assert.Contains("--- stdout ---", text);
        Assert.Contains("done", text);
        Assert.Contains("A out/report.md", text);
        Assert.Contains("不要声称已经写入真实工作区", text);

        var call = Assert.Single(harness.Jobs.Calls);
        Assert.Equal("session", call.SessionId);
        Assert.Equal("sub", call.WorkingDirectory);
        Assert.Equal(harness.Workspace, call.WorkspaceRoot);
        Assert.Equal(30, call.TimeoutSeconds);

        var audit = Assert.Single(harness.Audit.Entries);
        Assert.Equal("job-1", audit.JobId);
        Assert.Equal("ApproveOnce", audit.Approval);
        Assert.Equal(0, audit.ExitCode);
        Assert.Equal(1, audit.ChangeCount);
    }

    [Fact]
    public async Task 超时结果说明进程树已终止()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Jobs.Outcome = Outcome(
            new SandboxResult
            {
                JobId = "job-2",
                Kind = SandboxKind.AppContainer,
                ExitCode = 0,
                TimedOut = true,
                StdOut = "partial",
                Duration = TimeSpan.FromSeconds(30)
            });

        var text = await harness.RunAsync("Start-Sleep 60");

        Assert.Contains("超时：是", text);
        Assert.Contains("已终止进程树", text);
    }

    [Fact]
    public async Task 在工作区子目录中真实执行且不直写真实工作区()
    {
        using var host = new SandboxTestHost();
        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-pack-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "sub"));
        File.WriteAllText(Path.Combine(workspace, "sub", "a.txt"), "old\n");

        try
        {
            host.Settings.Current.Tools.SetPackEnabled(PowerShellToolPack.PackId, true);
            var approvals = new RecordingApprovalService(ToolApprovalDecision.ApproveOnce);
            var audit = new RecordingAuditLog();
            var context = new ToolContext(
                "session-e2e",
                workspace,
                [],
                host.Settings,
                approvals,
                host.Jobs,
                audit);

            var pack = new PowerShellToolPack(new RecordingDirectRunner());
            var text = await pack.RunPowerShellAsync(
                context,
                "$before = Get-Content -Raw -Path 'a.txt'\n"
                + "Set-Content -Path 'a.txt' -Value 'new' -Encoding UTF8\n"
                + "Write-Output ('before=' + $before.Trim())",
                workingDirectory: "sub",
                timeoutSeconds: 30);

            Assert.Contains("before=old", text);
            Assert.Contains("M sub/a.txt", text);
            Assert.DoesNotContain("before=new", text);

            // 真实工作区在用户应用变更前必须保持原样。
            Assert.Equal("old\n", File.ReadAllText(Path.Combine(workspace, "sub", "a.txt")));

            var job = Assert.Single(audit.Entries);
            Assert.Equal(SandboxKind.AppContainer, job.Sandbox);
            Assert.StartsWith("sha256:", job.ScriptSha256);
            Assert.Equal(1, job.ChangeCount);
            Assert.NotNull(approvals.Request);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }

    [Fact]
    public async Task 聊天室关掉沙箱后在工作区直跑并写直跑审计()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Direct.Result = new DirectRunResult(0, "done\n", string.Empty, false, false, 12, false, false);
        const string script = "Set-Content -Path a.txt -Value 'hi' -Encoding UTF8";

        var text = await harness.RunAsync(script, "sub", harness.DirectScope());

        Assert.Contains("直跑（无沙箱）：./sub", text);
        Assert.Contains("done", text);
        Assert.Contains("改动已经直接写入真实工作区", text);

        // 直跑不碰沙箱作业，也不检查沙箱可用性。
        Assert.Empty(harness.Jobs.Calls);

        var call = Assert.Single(harness.Direct.Calls);
        Assert.EndsWith("powershell.exe", call.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-EncodedCommand", call.Arguments);
        Assert.Equal(Path.Combine(harness.Workspace, "sub"), call.WorkingDirectory, ignoreCase: true);

        // 命令行传的是原文：Base64 里解出来必须和模型给的脚本一致。
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[^1])));

        // 审批框要明确这是直跑，不是沙箱副本。
        var request = Assert.IsType<ToolApprovalRequest>(harness.Approvals.Request);
        Assert.Equal("直跑 PowerShell 脚本（无沙箱）", request.Title);
        Assert.Contains("不隔离网络", request.Summary);

        var audit = Assert.Single(harness.Audit.Entries);
        Assert.Equal(SandboxKind.None, audit.Sandbox);
        Assert.Equal("direct", audit.Mode);
        Assert.Equal(0, audit.ExitCode);
        Assert.Equal("./sub", audit.WorkingDirectory);
    }

    [Fact]
    public async Task 直跑同样要过审批拒绝后不执行()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Approvals.Decision = ToolApprovalDecision.Deny;

        var text = await harness.RunAsync("Remove-Item a.txt", directory: null, harness.DirectScope());

        Assert.Contains("拒绝了", text);
        Assert.Empty(harness.Direct.Calls);
        Assert.Empty(harness.Jobs.Calls);
    }

    [Fact]
    public async Task 直跑超时说明已终止进程树()
    {
        var harness = new Harness();
        harness.Enable();
        harness.Direct.Result = new DirectRunResult(0, "partial", string.Empty, true, false, 30_000, false, false);

        var text = await harness.RunAsync("Start-Sleep 60", directory: null, harness.DirectScope());

        Assert.Contains("已超时并终止进程树", text);
        Assert.Contains("partial", text);
    }

    private static SandboxJobOutcome Outcome(SandboxResult result, params SandboxChange[] changes) =>
        new("session", result.JobId, result, changes, "jobs/job")
        {
            WorkspaceRoot = Path.GetTempPath()
        };

    private sealed class Harness
    {
        public Harness()
        {
            Workspace = Path.Combine(Path.GetTempPath(), "sdemo-pack", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Path.Combine(Workspace, "sub"));
        }

        public string Workspace { get; }

        public TestSettingsService Settings { get; } = new();

        public RecordingSandboxJobService Jobs { get; } = new();

        public RecordingApprovalService Approvals { get; } = new() { Decision = ToolApprovalDecision.ApproveOnce };

        public RecordingAuditLog Audit { get; } = new();

        public RecordingDirectRunner Direct { get; } = new();

        public void Enable() => Settings.Current.Tools.SetPackEnabled(PowerShellToolPack.PackId, true);

        /// <summary>聊天室关掉沙箱的作用域（其余字段沿用通用设置）。</summary>
        public EffectiveSettings DirectScope() => EffectiveSettings.Resolve(
            Settings.Current,
            workspace: null,
            session: new ChatSession
            {
                Id = "session",
                Title = "直跑",
                ScopedSettings = new ScopedSettings { SandboxEnabled = false }
            });

        public Task<string> RunAsync(string script, string? directory = null, EffectiveSettings? scope = null) =>
            new PowerShellToolPack(Direct).RunPowerShellAsync(
                new ToolContext("session", Workspace, [], Settings, Approvals, Jobs, Audit, Scope: scope),
                script,
                directory,
                timeoutSeconds: 30);
    }

    private sealed class RecordingSandboxJobService : ISandboxJobService
    {
#pragma warning disable CS0067
        public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;
#pragma warning restore CS0067

        public SandboxAvailability Availability { get; set; } = new(
            SandboxKind.AppContainer,
            Available: true,
            Verified: true,
            Reason: null,
            Detail: null);

        public SandboxJobOutcome Outcome { get; set; } = new(
            "session",
            "job",
            new SandboxResult
            {
                JobId = "job",
                Kind = SandboxKind.AppContainer,
                ExitCode = 0
            },
            [],
            "jobs/job");

        public List<(string SessionId, string Script, string? WorkingDirectory, int TimeoutSeconds, string? WorkspaceRoot)>
            Calls { get; } = [];

        public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<SandboxAvailability> GetAvailabilityAsync(
            SandboxKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability with { Kind = kind });

        public Task<SandboxJobOutcome> RunPowerShellAsync(
            string sessionId,
            string script,
            string? workingDirectory,
            int timeoutSeconds,
            string? workspaceRoot = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, script, workingDirectory, timeoutSeconds, workspaceRoot));
            return Task.FromResult(Outcome);
        }

        public Task<PdfExtraction> ExtractPdfAsync(
            string path,
            string? pages,
            int maxChars,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WindowsSandboxSelfTestReport> RunWindowsSandboxSelfTestAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingApprovalService(ToolApprovalDecision decision = ToolApprovalDecision.ApproveOnce)
        : IToolApprovalService
    {
        public ToolApprovalRequest? Request { get; private set; }

        public ToolApprovalDecision Decision { get; set; } = decision;

        public ToolApprovalRequest? Current => null;

#pragma warning disable CS0067
        public event Action? Changed;
#pragma warning restore CS0067

        public Task<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(Decision);
        }

        public void Resolve(ToolApprovalDecision decision)
        {
        }

        public void CancelAll()
        {
        }
    }

    private sealed class RecordingAuditLog : IToolAuditLog
    {
        public List<ToolAuditEntry> Entries { get; } = [];

        public void Write(ToolAuditEntry entry) => Entries.Add(entry);
    }

    /// <summary>记录直跑参数但不真的起进程。</summary>
    private sealed class RecordingDirectRunner : IDirectProcessRunner
    {
        public DirectRunResult Result { get; set; } = new(0, string.Empty, string.Empty, false, false, 0, false, false);

        public List<(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

        public Task<DirectRunResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            int maxOutputChars,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add((executable, arguments, workingDirectory));
            return Task.FromResult(Result);
        }
    }
}
