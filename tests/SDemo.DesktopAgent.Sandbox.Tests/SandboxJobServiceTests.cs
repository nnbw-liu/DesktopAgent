using System.IO;
using SDemo.DesktopAgent.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S2 端到端：真实 AppContainer 中启动 SandboxHost，脚本只改 work 副本，
/// 宿主通过 manifest 计算 diff，真实工作区在 apply 前保持不变。
/// </summary>
public sealed class SandboxJobServiceTests
{
    [Fact]
    public async Task AppContainer作业返回结果与变更集且不直写工作区()
    {
        using var host = new SandboxTestHost();
        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-job-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "old\n");

        try
        {
            // S3 起 workingDirectory 是“相对工作区的目录”；工作区由 WorkspaceContext 固定。
            host.Workspace.RootPath = workspace;

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "$before = Get-Content -Raw -Path 'a.txt'\n"
                + "Set-Content -Path 'a.txt' -Value 'new' -Encoding UTF8\n"
                + "Write-Output ('before=' + $before.Trim())\n"
                + "Write-Output 'done'",
                workingDirectory: ".",
                timeoutSeconds: 30);

            Assert.True(
                outcome.Result.Failure == SandboxFailureCode.None,
                $"failure={outcome.Result.Failure} message={outcome.Result.Message}\n" +
                $"stdout={outcome.Result.StdOut}\nstderr={outcome.Result.StdErr}\n" +
                $"job={outcome.JobDirectory}");
            Assert.Equal(0, outcome.Result.ExitCode);
            Assert.Contains("before=old", outcome.Result.StdOut);
            Assert.Contains("done", outcome.Result.StdOut);
            Assert.Equal(workspace, outcome.WorkspaceRoot);
            Assert.Equal("old\n", File.ReadAllText(Path.Combine(workspace, "a.txt")));

            var workFile = Path.Combine(outcome.JobDirectory, "work", "a.txt");
            var baseFile = Path.Combine(outcome.JobDirectory, "base", "a.txt");
            Assert.True(
                outcome.Changes.Count == 1,
                $"changes={outcome.Changes.Count} work={ReadOrMissing(workFile)} base={ReadOrMissing(baseFile)} "
                + $"manifest={ReadOrMissing(Path.Combine(outcome.JobDirectory, "manifest.json"))} "
                + $"stdout={outcome.Result.StdOut} stderr={outcome.Result.StdErr}");

            var change = Assert.Single(outcome.Changes);
            Assert.Equal("a.txt", change.RelativePath);
            Assert.Equal(SandboxChangeKind.Modified, change.Kind);
            Assert.Contains("+new", change.Diff);
            Assert.True(File.Exists(Path.Combine(outcome.JobDirectory, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(outcome.JobDirectory, "work", "a.txt")));
            Assert.True(File.Exists(Path.Combine(outcome.JobDirectory, "base", "a.txt")));
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
    public async Task 工作区超大时拒绝执行且不启动SandboxHost()
    {
        using var host = new SandboxTestHost();
        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-job-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "large.bin"), new string('x', 2 * 1024 * 1024));

        try
        {
            host.Settings.Current.Sandbox.WorkspaceCopyLimitMB = 1;
            host.Workspace.RootPath = workspace;

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "Write-Output 'never'",
                workingDirectory: ".",
                timeoutSeconds: 30);

            Assert.Equal(SandboxFailureCode.WorkspaceTooLarge, outcome.Result.Failure);
            Assert.Contains("工作区过大", outcome.Result.Message);
            Assert.Empty(outcome.Changes);
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

    /// <summary>
    /// P1 回归位：AppContainer 内 PowerShell 当前目录是 SDEMO: PSDrive，
    /// Remove-Item 相对路径会 Access denied；修复 provider / 位置后再启用。
    /// 详见 docs/sandbox-ui-acceptance-s6.md。
    /// </summary>
    [Fact(Skip = "P1：SDEMO PSDrive 下 Remove-Item 失败，待修复后启用")]
    public async Task AppContainer作业删除文件产生Deleted变更()
    {
        using var host = new SandboxTestHost();
        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-job-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "doomed.txt"), "delete me\n");

        try
        {
            host.Workspace.RootPath = workspace;

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "Remove-Item -LiteralPath 'doomed.txt' -Force\nWrite-Output 'deleted'",
                workingDirectory: ".",
                timeoutSeconds: 30);

            Assert.DoesNotContain("Access is denied", outcome.Result.StdErr);
            Assert.Contains("deleted", outcome.Result.StdOut);
            var change = Assert.Single(outcome.Changes);
            Assert.Equal(SandboxChangeKind.Deleted, change.Kind);
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
    public async Task 相对子目录作为工作目录且变更仍按工作区根计算()
    {
        using var host = new SandboxTestHost();
        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-job-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "sub"));
        File.WriteAllText(Path.Combine(workspace, "sub", "a.txt"), "old\n");

        try
        {
            host.Workspace.RootPath = workspace;

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "Set-Content -Path 'a.txt' -Value 'new' -Encoding UTF8\nWrite-Output 'sub-ok'",
                workingDirectory: "sub",
                timeoutSeconds: 30);

            Assert.True(
                outcome.Result.Failure == SandboxFailureCode.None,
                $"failure={outcome.Result.Failure} message={outcome.Result.Message} stdout={outcome.Result.StdOut}");
            Assert.Contains("sub-ok", outcome.Result.StdOut);
            var change = Assert.Single(outcome.Changes);
            Assert.Equal("sub/a.txt", change.RelativePath);
            Assert.Equal(SandboxChangeKind.Modified, change.Kind);
            Assert.Equal("old\n", File.ReadAllText(Path.Combine(workspace, "sub", "a.txt")));
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

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\Windows")]
    public async Task 工作目录逃逸时拒绝执行(string workingDirectory)
    {
        using var host = new SandboxTestHost();
        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-job-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            host.Workspace.RootPath = workspace;

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "Write-Output 'never'",
                workingDirectory,
                timeoutSeconds: 30);

            Assert.Equal(SandboxFailureCode.PathEscape, outcome.Result.Failure);
            Assert.Empty(outcome.Changes);
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
    public async Task 强制WindowsSandbox但不可用时拒绝执行()
    {
        using var host = new SandboxTestHost();
        var availability = await host.Jobs.GetAvailabilityAsync(SandboxKind.WindowsSandbox);
        if (availability.Available)
        {
            // 已启用 WSB 的机器上会真的启动虚拟机，交给手工验收。
            return;
        }

        host.Settings.Current.Sandbox.Preferred = SandboxKind.WindowsSandbox;
        host.Settings.Current.Sandbox.Requirement = SandboxRequirement.Required;

        var outcome = await host.Jobs.RunPowerShellAsync(
            "session-1",
            "Write-Output 'never'",
            workingDirectory: ".",
            timeoutSeconds: 30);

        Assert.Equal(SandboxFailureCode.NotAvailable, outcome.Result.Failure);
        Assert.Contains("已拒绝降级执行", outcome.Result.Message);
        Assert.Contains("Windows Sandbox", outcome.Result.Message);
        Assert.Empty(outcome.Changes);
        Assert.Empty(outcome.JobDirectory);
    }

    [Fact]
    public async Task 自动模式首选WindowsSandbox不可用时降级到AppContainer()
    {
        using var host = new SandboxTestHost();
        var availability = await host.Jobs.GetAvailabilityAsync(SandboxKind.WindowsSandbox);
        if (availability.Available)
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "sdemo-job-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            host.Workspace.RootPath = workspace;
            host.Settings.Current.Sandbox.Preferred = SandboxKind.WindowsSandbox;
            host.Settings.Current.Sandbox.Requirement = SandboxRequirement.Preferred;

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "Write-Output 'fallback-ok'",
                workingDirectory: ".",
                timeoutSeconds: 30);

            Assert.True(
                outcome.Result.Failure == SandboxFailureCode.None,
                $"failure={outcome.Result.Failure} message={outcome.Result.Message}");
            Assert.Equal(SandboxKind.AppContainer, outcome.Result.Kind);
            Assert.Contains("fallback-ok", outcome.Result.StdOut);
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

    private static string ReadOrMissing(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : "<missing>";
}
