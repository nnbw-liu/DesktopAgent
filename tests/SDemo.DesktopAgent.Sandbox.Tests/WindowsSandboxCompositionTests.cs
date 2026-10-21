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
    public async Task WindowsSandbox运行器报告本机的真实检测结果()
    {
        using var host = new SandboxTestHost();
        var runner = host.Provider.GetServices<ISandboxRunner>()
            .Single(candidate => candidate.Kind == SandboxKind.WindowsSandbox);

        var availability = await runner.ProbeAsync();

        Assert.Equal(SandboxKind.WindowsSandbox, availability.Kind);
        Assert.False(availability.Verified);

        if (availability.Available)
        {
            // 功能已启用的机器：可用但尚未自检。
            Assert.Contains("自检", availability.Reason);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(availability.Reason));
        }
    }

    [Fact]
    public async Task 作业服务能按类型查询可用性()
    {
        using var host = new SandboxTestHost();

        var appContainer = await host.Jobs.GetAvailabilityAsync(SandboxKind.AppContainer);
        var windowsSandbox = await host.Jobs.GetAvailabilityAsync(SandboxKind.WindowsSandbox);

        Assert.Equal(SandboxKind.AppContainer, appContainer.Kind);
        Assert.Equal(SandboxKind.WindowsSandbox, windowsSandbox.Kind);
        Assert.False(windowsSandbox.Verified);
    }

    [Fact]
    public async Task WindowsSandbox不可用时自检给出原因而不是抛异常()
    {
        using var host = new SandboxTestHost();
        var availability = await host.Jobs.GetAvailabilityAsync(SandboxKind.WindowsSandbox);
        if (availability.Available)
        {
            // 已启用 WSB 的机器上真的会启动虚拟机，交给手工验收，不在普通测试里跑。
            return;
        }

        var report = await host.Jobs.RunWindowsSandboxSelfTestAsync();

        Assert.False(report.Launched);
        Assert.False(report.AllPassed);
        Assert.False(string.IsNullOrWhiteSpace(report.Detail));
    }
}
