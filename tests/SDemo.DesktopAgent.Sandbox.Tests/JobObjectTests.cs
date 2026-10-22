using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>Job Object 的进程数 / 内存限制：超限的创建与分配必须失败，而不是被静默忽略。</summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class JobObjectTests
{
    [Fact]
    public async Task 进程数上限阻止创建新的子进程()
    {
        using var host = new SandboxTestHost();
        var layout = CreateJob(host, "job-00000011");
        var script =
            "try { Start-Process -FilePath \"$env:SystemRoot\\System32\\cmd.exe\" " +
            "-ArgumentList '/c','exit 0' -NoNewWindow -Wait; exit 0 } catch { exit 7 }";

        var result = await host.Runner.RunAsync(Request(layout, script, new SandboxLimits
        {
            TimeoutSeconds = 20,
            MaxProcesses = 1,
            MemoryLimitMB = 512,
            JobMemoryLimitMB = 1024
        }));

        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task 内存上限让超额分配失败()
    {
        using var host = new SandboxTestHost();
        var layout = CreateJob(host, "job-00000012");
        var script =
            "try { $a = New-Object byte[] (300MB); exit 0 } catch { exit 7 }";

        var result = await host.Runner.RunAsync(Request(layout, script, new SandboxLimits
        {
            TimeoutSeconds = 30,
            MaxProcesses = 8,
            MemoryLimitMB = 128,
            JobMemoryLimitMB = 256
        }));

        Assert.Equal(7, result.ExitCode);
    }

    private static SandboxJobLayout CreateJob(SandboxTestHost host, string jobId)
    {
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);
        host.SandboxDirectory.EnsureRoot(profile!.Sid);
        return host.SandboxDirectory.EnsureJob(jobId, profile.Sid);
    }

    private static SandboxRequest Request(SandboxJobLayout layout, string script, SandboxLimits limits) =>
        new()
        {
            JobId = layout.JobId,
            Kind = SandboxKind.AppContainer,
            Executable = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script
            ],
            WorkingDirectory = layout.WorkDirectory,
            OutputDirectory = layout.OutputDirectory,
            Limits = limits
        };
}
