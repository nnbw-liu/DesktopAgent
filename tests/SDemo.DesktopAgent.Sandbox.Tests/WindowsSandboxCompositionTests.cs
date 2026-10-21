using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using SDemo.DesktopAgent.Platform.Windows.Composition;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>S0 验收：Windows 宿主能解析沙箱服务，且所有运行器都明确报告不可用。</summary>
public sealed class WindowsSandboxCompositionTests
{
    [Fact]
    public void 沙箱服务都能从容器解析()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<ISandboxJobService>());
        Assert.NotNull(provider.GetRequiredService<ISandboxSelector>());
        Assert.NotNull(provider.GetRequiredService<SandboxJanitor>());
        Assert.NotNull(provider.GetRequiredService<AppContainerProfile>());

        // 默认 ISandboxRunner 是 AppContainer（S1 的主路线）。
        Assert.Equal(SandboxKind.AppContainer, provider.GetRequiredService<ISandboxRunner>().Kind);
    }

    [Fact]
    public async Task 两个运行器都注册进了集合且都不可用()
    {
        using var provider = BuildProvider();
        var runners = provider.GetServices<ISandboxRunner>().ToArray();

        Assert.Equal(2, runners.Length);
        Assert.Contains(runners, runner => runner.Kind == SandboxKind.AppContainer);
        Assert.Contains(runners, runner => runner.Kind == SandboxKind.WindowsSandbox);

        var availability = await provider.GetRequiredService<ISandboxJobService>().GetAvailabilityAsync();

        Assert.False(availability.Available);
        Assert.False(availability.Verified);
        Assert.Equal(SandboxKind.None, availability.Kind);
        Assert.Contains("S1", availability.Reason);
        Assert.Contains("S4", availability.Reason);
    }

    [Fact]
    public async Task 占位作业服务返回NotAvailable且不产生变更()
    {
        using var provider = BuildProvider();
        var jobs = provider.GetRequiredService<ISandboxJobService>();

        var outcome = await jobs.RunPowerShellAsync(
            sessionId: "session-1",
            script: "Write-Output 1",
            workingDirectory: null,
            timeoutSeconds: 30);

        Assert.Equal(SandboxFailureCode.NotAvailable, outcome.Result.Failure);
        Assert.Empty(outcome.Changes);
        Assert.Equal(string.Empty, outcome.JobDirectory);
        Assert.Equal("session-1", outcome.SessionId);
    }

    [Fact]
    public async Task 占位作业服务的PDF解析返回失败原因()
    {
        using var provider = BuildProvider();
        var jobs = provider.GetRequiredService<ISandboxJobService>();

        var extraction = await jobs.ExtractPdfAsync("input/document.pdf", pages: null, maxChars: 20_000);

        Assert.False(extraction.Ok);
        Assert.Equal(0, extraction.PageCount);
        Assert.False(string.IsNullOrWhiteSpace(extraction.Message));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddWindowsSandbox();
        return services.BuildServiceProvider();
    }
}
