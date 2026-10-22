using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>PowerShell 结果格式化：模型能看到的是什么、底气在哪。</summary>
public sealed class PowerShellResultFormatterTests
{
    [Fact]
    public void 正常结果包含退出码输出与变更列表()
    {
        var outcome = Outcome(
            Result(exitCode: 0, stdOut: "done\n", stdErr: string.Empty),
            SandboxChangeKind.Added,
            SandboxChangeKind.Modified,
            SandboxChangeKind.Deleted);

        var text = PowerShellResultFormatter.Format(outcome);

        Assert.Contains("沙箱：AppContainer", text);
        Assert.Contains("退出码：0", text);
        Assert.Contains("--- stdout ---", text);
        Assert.Contains("done", text);
        Assert.Contains("--- 沙箱内改动（尚未写入真实工作区）---", text);
        Assert.Contains("A note.txt", text);
        Assert.Contains("M app.cs", text);
        Assert.Contains("D old.txt", text);
        Assert.Contains("不要声称已经写入真实工作区", text);
    }

    [Fact]
    public void 无变更时明确说明()
    {
        var text = PowerShellResultFormatter.Format(Outcome(Result(0, "ok", string.Empty)));

        Assert.Contains("沙箱内没有文件改动", text);
    }

    [Fact]
    public void 超时结果说明进程树已终止()
    {
        var result = Result(0, "partial", string.Empty) with { TimedOut = true };

        var text = PowerShellResultFormatter.Format(Outcome(result));

        Assert.Contains("超时：是", text);
        Assert.Contains("已终止进程树", text);
    }

    [Fact]
    public void 大输出截断并标记()
    {
        var stdout = new string('x', PowerShellResultFormatter.MaxOutputCharsPerStream + 10);

        var text = PowerShellResultFormatter.Format(Outcome(Result(0, stdout, string.Empty)));

        Assert.Contains("输出已截断", text);
        Assert.DoesNotContain(new string('x', PowerShellResultFormatter.MaxOutputCharsPerStream + 1), text);
    }

    [Fact]
    public void 沙箱不可用时返回ERROR()
    {
        var result = Result(exitCode: -1, stdOut: string.Empty, stdErr: string.Empty) with
        {
            Failure = SandboxFailureCode.NotAvailable,
            Message = "没有可用沙箱。"
        };

        var text = PowerShellResultFormatter.Format(Outcome(result));

        Assert.StartsWith("ERROR: 沙箱不可用", text);
        Assert.Contains("没有可用沙箱", text);
    }

    [Theory]
    [InlineData(SandboxFailureCode.WorkspaceTooLarge, "ERROR: 工作区过大")]
    [InlineData(SandboxFailureCode.ProtocolError, "ERROR: 沙箱返回了无效结果")]
    [InlineData(SandboxFailureCode.LaunchFailed, "ERROR: 沙箱启动失败")]
    [InlineData(SandboxFailureCode.PathEscape, "ERROR: 路径超出允许范围")]
    public void 特定失败码有明确提示(SandboxFailureCode failure, string expected)
    {
        var result = Result(-1, string.Empty, string.Empty) with
        {
            Failure = failure,
            Message = "细节"
        };

        Assert.StartsWith(expected, PowerShellResultFormatter.Format(Outcome(result)));
    }

    private static SandboxResult Result(int exitCode, string stdOut, string stdErr) => new()
    {
        JobId = "job",
        Kind = SandboxKind.AppContainer,
        ExitCode = exitCode,
        StdOut = stdOut,
        StdErr = stdErr,
        Duration = TimeSpan.FromMilliseconds(812)
    };

    private static SandboxJobOutcome Outcome(SandboxResult result, params SandboxChangeKind[] kinds)
    {
        IReadOnlyList<SandboxChange> changes = kinds.Length == 0
            ? []
            : kinds.Select(kind => new SandboxChange(
                kind == SandboxChangeKind.Added ? "note.txt"
                : kind == SandboxChangeKind.Modified ? "app.cs"
                : "old.txt",
                kind,
                10,
                IsBinary: false,
                Diff: null)).ToArray();

        return new SandboxJobOutcome("session", "job", result, changes, "jobs/job")
        {
            WorkspaceRoot = "workspace"
        };
    }
}
