using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>S0 占位实现的行为：永不假装可用，也不会误创建 profile。</summary>
public sealed class SandboxPlaceholderTests
{
    [Fact]
    public async Task AppContainer运行器报告不可用()
    {
        var runner = new AppContainerSandboxRunner();

        var availability = await runner.ProbeAsync();

        Assert.Equal(SandboxKind.AppContainer, availability.Kind);
        Assert.False(availability.Available);
        Assert.False(availability.Verified);
        Assert.Contains("S1", availability.Reason);
    }

    [Fact]
    public async Task WindowsSandbox运行器报告不可用()
    {
        var runner = new WindowsSandboxRunner();

        var availability = await runner.ProbeAsync();

        Assert.Equal(SandboxKind.WindowsSandbox, availability.Kind);
        Assert.False(availability.Available);
        Assert.Contains("S4", availability.Reason);
    }

    [Fact]
    public async Task 运行器直接调用也返回NotAvailable()
    {
        var runner = new AppContainerSandboxRunner();

        var result = await runner.RunAsync(new SandboxRequest
        {
            JobId = "job-00000004",
            Kind = SandboxKind.AppContainer,
            Executable = "cmd.exe",
            WorkingDirectory = "work",
            OutputDirectory = "output"
        });

        Assert.Equal(SandboxFailureCode.NotAvailable, result.Failure);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public void 占位profile创建会明确拒绝()
    {
        Assert.Throws<NotSupportedException>(() => AppContainerProfile.Ensure());
    }

    [Fact]
    public async Task 清理器当前是空操作()
    {
        var janitor = new SandboxJanitor();

        await janitor.CleanupAsync();
    }
}
