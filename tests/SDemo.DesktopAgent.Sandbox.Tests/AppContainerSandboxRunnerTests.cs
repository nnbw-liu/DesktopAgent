using System.Diagnostics;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>真机集成测试：真实 CreateProcess / AppContainer / Job Object。</summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class AppContainerSandboxRunnerTests
{
    [Fact]
    public async Task 运行cmd并捕获输出()
    {
        using var host = new SandboxTestHost();
        var layout = Prepare(host, "job-00000001");
        var result = await host.Runner.RunAsync(Request(
            layout,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/c", "echo SDEMO_RUNNER_OK"]));

        Assert.Equal(SandboxFailureCode.None, result.Failure);
        Assert.Equal(SandboxKind.AppContainer, result.Kind);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SDEMO_RUNNER_OK", result.StdOut);
        Assert.Contains("stdout.txt", result.Artifacts);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task 工作目录越界直接拒绝()
    {
        using var host = new SandboxTestHost();
        var layout = Prepare(host, "job-00000002");
        var request = Request(layout, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo x"]) with
        {
            WorkingDirectory = host.Root
        };

        var result = await host.Runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.PathEscape, result.Failure);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task 非法JobId直接拒绝()
    {
        using var host = new SandboxTestHost();
        var request = new SandboxRequest
        {
            JobId = "bad id",
            Kind = SandboxKind.AppContainer,
            Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = Path.Combine(host.Paths.SandboxRoot, "work"),
            OutputDirectory = Path.Combine(host.Paths.SandboxRoot, "output")
        };

        var result = await host.Runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.ProtocolError, result.Failure);
    }

    [Fact]
    public async Task 超时会终止进程树并返回Timeout()
    {
        using var host = new SandboxTestHost();
        var layout = Prepare(host, "job-00000003");
        var stopwatch = Stopwatch.StartNew();

        var result = await host.Runner.RunAsync(Request(
            layout,
            PowerShellPath(),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            timeoutSeconds: 2));

        stopwatch.Stop();

        Assert.True(result.TimedOut, result.Message);
        Assert.Equal(SandboxFailureCode.Timeout, result.Failure);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"超时终止耗时过长：{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task 取消后进程树被终止()
    {
        using var host = new SandboxTestHost();
        var layout = Prepare(host, "job-00000006");
        var marker = Path.Combine(layout.WorkDirectory, "cancel-marker.txt");
        var script = $"Start-Sleep -Seconds 5; Set-Content -Path '{marker}' -Value done";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await host.Runner.RunAsync(
            Request(layout, PowerShellPath(), ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]),
            cancellation.Token);

        Assert.True(result.Canceled, result.Message);
        Assert.Equal(SandboxFailureCode.Canceled, result.Failure);

        // 如果进程树没被杀掉，5 秒后它会写出标记文件。
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.False(File.Exists(marker), "取消后仍有子进程存活并写入了标记文件。");
    }

    [Fact]
    public async Task 不能读取用户目录里的文件()
    {
        using var host = new SandboxTestHost();
        var layout = Prepare(host, "job-00000004");
        var sentinel = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"sdemo-runner-sentinel-{Guid.NewGuid():N}.txt");
        File.WriteAllText(sentinel, "RUNNER_SECRET");

        try
        {
            var result = await host.Runner.RunAsync(Request(
                layout,
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/c", $"type \"{sentinel}\""]));

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("RUNNER_SECRET", result.StdOut);
        }
        finally
        {
            TryDelete(sentinel);
        }
    }

    [Fact]
    public async Task 不能写工作区()
    {
        using var host = new SandboxTestHost();
        var layout = Prepare(host, "job-00000005");
        Directory.CreateDirectory(host.Paths.DefaultWorkspaceRoot);
        var sentinel = Path.Combine(host.Paths.DefaultWorkspaceRoot, "sdemo-workspace-sentinel.txt");
        File.WriteAllText(sentinel, "WORKSPACE_SENTINEL");

        try
        {
            var result = await host.Runner.RunAsync(Request(
                layout,
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/c", $"echo x > \"{sentinel}\""]));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("WORKSPACE_SENTINEL", File.ReadAllText(sentinel));
        }
        finally
        {
            TryDelete(sentinel);
        }
    }

    private static SandboxJobLayout Prepare(SandboxTestHost host, string jobId)
    {
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);
        host.SandboxDirectory.EnsureRoot(profile!.Sid);
        return host.SandboxDirectory.EnsureJob(jobId, profile.Sid);
    }

    private static SandboxRequest Request(
        SandboxJobLayout layout,
        string executable,
        IReadOnlyList<string> arguments,
        int timeoutSeconds = 30) =>
        new()
        {
            JobId = layout.JobId,
            Kind = SandboxKind.AppContainer,
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = layout.WorkDirectory,
            OutputDirectory = layout.OutputDirectory,
            Limits = new SandboxLimits
            {
                TimeoutSeconds = timeoutSeconds,
                MaxOutputBytes = 256 * 1024,
                MemoryLimitMB = 1024,
                JobMemoryLimitMB = 2048
            }
        };

    private static string PowerShellPath() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言。
        }
    }
}
