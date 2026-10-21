using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class SandboxProbeTests
{
    [Fact]
    public async Task 探针报告AppContainer可用且已通过PowerShell验证()
    {
        using var host = new SandboxTestHost();

        var availability = await host.Runner.ProbeAsync();

        Assert.True(availability.Available, availability.Reason);
        Assert.True(availability.Verified, availability.Detail);
        Assert.Equal(SandboxKind.AppContainer, availability.Kind);
        Assert.Null(availability.Reason);
    }

    [Fact]
    public async Task 设置页自检全部符合预期()
    {
        using var host = new SandboxTestHost();

        var report = await host.Jobs.RunSelfTestAsync();

        Assert.True(report.CmdOk, report.Detail);
        Assert.True(report.PowerShellOk, report.Detail);
        Assert.True(report.CannotReadUserProfile, report.Detail);
        Assert.True(report.CannotWriteWorkspace, report.Detail);
        Assert.True(report.NoNetwork, report.Detail);
        Assert.True(report.SubprocessOk, report.Detail);
        Assert.True(report.TimeoutOk, report.Detail);
        Assert.True(report.AllPassed, report.Detail);
    }

    [Fact]
    public async Task 探针结果缓存到重置为止()
    {
        using var host = new SandboxTestHost();

        var first = await host.Runner.ProbeAsync();
        var second = await host.Runner.ProbeAsync();
        Assert.Same(first, second);

        host.Runner.Probe.ResetCache();
        var third = await host.Runner.ProbeAsync();
        Assert.NotSame(second, third);
    }

    [Fact]
    public async Task 选择器返回自检通过的沙箱()
    {
        using var host = new SandboxTestHost();
        var selector = host.Provider.GetRequiredService<ISandboxSelector>();

        var availability = await selector.GetAvailabilityAsync();

        Assert.True(availability.Available, availability.Reason);
        Assert.Equal(SandboxKind.AppContainer, availability.Kind);
        Assert.True(availability.Verified, availability.Detail);
    }
}
