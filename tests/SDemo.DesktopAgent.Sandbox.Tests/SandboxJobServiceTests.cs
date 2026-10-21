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
            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "$before = Get-Content -Raw -Path 'a.txt'\n"
                + "Set-Content -Path 'a.txt' -Value 'new' -Encoding UTF8\n"
                + "Write-Output ('before=' + $before.Trim())\n"
                + "Write-Output 'done'",
                workspace,
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

            var outcome = await host.Jobs.RunPowerShellAsync(
                "session-1",
                "Write-Output 'never'",
                workspace,
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

    private static string ReadOrMissing(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : "<missing>";
}
