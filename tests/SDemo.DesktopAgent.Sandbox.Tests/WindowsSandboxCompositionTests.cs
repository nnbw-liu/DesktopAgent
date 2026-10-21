using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>S1 验收：Windows 宿主能解析沙箱服务，默认运行器是 AppContainer。</summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class WindowsSandboxCompositionTests
{
    [Fact]
    public void 沙箱服务都能从容器解析()
    {
        using var host = new SandboxTestHost();
        var provider = host.Provider;

        Assert.NotNull(provider.GetRequiredService<ISandboxJobService>());
        Assert.NotNull(provider.GetRequiredService<ISandboxSelector>());
        Assert.NotNull(provider.GetRequiredService<SandboxJanitor>());
        Assert.NotNull(provider.GetRequiredService<AppContainerProfileProvider>());
        Assert.NotNull(provider.GetRequiredService<SandboxProbe>());
        Assert.NotNull(provider.GetRequiredService<SandboxDirectory>());

        // 默认 ISandboxRunner 是 AppContainer（S1 的主路线）。
        Assert.Equal(SandboxKind.AppContainer, provider.GetRequiredService<ISandboxRunner>().Kind);
    }

    [Fact]
    public void 两个运行器都注册进了集合()
    {
        using var host = new SandboxTestHost();
        var runners = host.Provider.GetServices<ISandboxRunner>().ToArray();

        Assert.Equal(2, runners.Length);
        Assert.Contains(runners, runner => runner.Kind == SandboxKind.AppContainer);
        Assert.Contains(runners, runner => runner.Kind == SandboxKind.WindowsSandbox);
    }

    [Fact]
    public void 自检探针与运行器共用同一个实例()
    {
        using var host = new SandboxTestHost();

        Assert.Same(host.Runner.Probe, host.Provider.GetRequiredService<SandboxProbe>());
    }

    [Fact]
    public async Task WindowsSandbox运行器仍明确报告S4未实现()
    {
        using var host = new SandboxTestHost();
        var runner = host.Provider.GetServices<ISandboxRunner>()
            .Single(candidate => candidate.Kind == SandboxKind.WindowsSandbox);

        var availability = await runner.ProbeAsync();

        Assert.False(availability.Available);
        Assert.False(availability.Verified);
        Assert.Contains("S4", availability.Reason);
    }
}
