using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>沙箱选择策略：首选优先、可用才执行、都不可用时拒绝（不得降级到无沙箱）。</summary>
public sealed class SandboxSelectorTests
{
    [Fact]
    public async Task 必需沙箱但不可用时拒绝执行()
    {
        var selector = new SandboxSelector([new FakeRunner(SandboxKind.AppContainer, available: false)]);

        var result = await selector.RunAsync(
            CreateRequest("job-00000001"),
            new SandboxPolicy(SandboxRequirement.Required, SandboxKind.AppContainer));

        Assert.Equal(SandboxFailureCode.NotAvailable, result.Failure);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("不可用", result.Message);
    }

    [Fact]
    public async Task 首选运行器可用时由它执行()
    {
        var appContainer = new FakeRunner(SandboxKind.AppContainer, available: true, exitCode: 7);
        var windowsSandbox = new FakeRunner(SandboxKind.WindowsSandbox, available: true, exitCode: 9);
        var selector = new SandboxSelector([appContainer, windowsSandbox]);

        var result = await selector.RunAsync(
            CreateRequest("job-00000002"),
            new SandboxPolicy(SandboxRequirement.Required, SandboxKind.WindowsSandbox));

        Assert.Equal(9, result.ExitCode);
        Assert.Equal(1, windowsSandbox.RunCount);
        Assert.Equal(0, appContainer.RunCount);
        Assert.Equal(SandboxKind.WindowsSandbox, windowsSandbox.LastRequest?.Kind);
    }

    [Fact]
    public async Task Preferred时首选不可用可回退到另一个可用运行器()
    {
        var appContainer = new FakeRunner(SandboxKind.AppContainer, available: true, exitCode: 7);
        var windowsSandbox = new FakeRunner(SandboxKind.WindowsSandbox, available: false);
        var selector = new SandboxSelector([appContainer, windowsSandbox]);

        var result = await selector.RunAsync(
            CreateRequest("job-00000003"),
            new SandboxPolicy(SandboxRequirement.Preferred, SandboxKind.WindowsSandbox));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(1, appContainer.RunCount);
        // 请求里的 Kind 要跟着选中的运行器改，不能让运行器执行一个“名义上不是自己”的请求。
        Assert.Equal(SandboxKind.AppContainer, appContainer.LastRequest?.Kind);
    }

    [Fact]
    public async Task Required时首选不可用不降级()
    {
        var appContainer = new FakeRunner(SandboxKind.AppContainer, available: true, exitCode: 7);
        var windowsSandbox = new FakeRunner(SandboxKind.WindowsSandbox, available: false);
        var selector = new SandboxSelector([appContainer, windowsSandbox]);

        var result = await selector.RunAsync(
            CreateRequest("job-00000004"),
            new SandboxPolicy(SandboxRequirement.Required, SandboxKind.WindowsSandbox));

        Assert.Equal(SandboxFailureCode.NotAvailable, result.Failure);
        Assert.Contains("已拒绝降级执行", result.Message);
        Assert.Equal(0, appContainer.RunCount);
    }

    [Fact]
    public async Task 没有可用运行器时原因可读()
    {
        var selector = new SandboxSelector(
        [
            new FakeRunner(SandboxKind.AppContainer, available: false),
            new FakeRunner(SandboxKind.WindowsSandbox, available: false)
        ]);

        var availability = await selector.GetAvailabilityAsync();

        Assert.False(availability.Available);
        Assert.False(availability.Verified);
        Assert.Equal(SandboxKind.None, availability.Kind);
        Assert.Contains("AppContainer", availability.Reason);
        Assert.Contains("WindowsSandbox", availability.Reason);
    }

    [Fact]
    public async Task 首选WindowsSandbox可用时选中它()
    {
        var appContainer = new FakeRunner(SandboxKind.AppContainer, available: true);
        var windowsSandbox = new FakeRunner(SandboxKind.WindowsSandbox, available: true);
        var selector = new SandboxSelector([appContainer, windowsSandbox]);

        var selection = await selector.SelectAsync(
            new SandboxPolicy(SandboxRequirement.Required, SandboxKind.WindowsSandbox));

        Assert.True(selection.IsAvailable);
        Assert.Same(windowsSandbox, selection.Runner);
        Assert.Equal(SandboxKind.WindowsSandbox, selection.Availability.Kind);
    }

    [Fact]
    public async Task 要求已验证时跳过未验证的沙箱()
    {
        var appContainer = new FakeRunner(SandboxKind.AppContainer, available: true, verified: true);
        var windowsSandbox = new FakeRunner(SandboxKind.WindowsSandbox, available: true, verified: false);
        var selector = new SandboxSelector([appContainer, windowsSandbox]);

        var selection = await selector.SelectAsync(
            new SandboxPolicy(SandboxRequirement.Preferred, SandboxKind.WindowsSandbox),
            requireVerified: true);

        // 自动模式：首选（WSB）没通过自检，允许换用已验证的 AppContainer。
        Assert.Same(appContainer, selection.Runner);
        Assert.Equal(SandboxKind.AppContainer, selection.Availability.Kind);
    }

    [Fact]
    public async Task 强制首选的沙箱未验证时拒绝降级()
    {
        var appContainer = new FakeRunner(SandboxKind.AppContainer, available: true, verified: true);
        var windowsSandbox = new FakeRunner(SandboxKind.WindowsSandbox, available: true, verified: false);
        var selector = new SandboxSelector([appContainer, windowsSandbox]);

        var selection = await selector.SelectAsync(
            new SandboxPolicy(SandboxRequirement.Required, SandboxKind.WindowsSandbox),
            requireVerified: true);

        Assert.Null(selection.Runner);
        Assert.False(selection.IsAvailable);
        Assert.Contains("已拒绝降级执行", selection.Availability.Reason);
        Assert.Contains("尚未通过自检", selection.Availability.Reason);
    }

    private static SandboxRequest CreateRequest(string jobId) => new()
    {
        JobId = jobId,
        Kind = SandboxKind.AppContainer,
        Executable = "cmd.exe",
        WorkingDirectory = "work",
        OutputDirectory = "output"
    };

    private sealed class FakeRunner(SandboxKind kind, bool available, int exitCode = 0, bool? verified = null)
        : ISandboxRunner
    {
        public SandboxKind Kind => kind;

        public int RunCount { get; private set; }

        public SandboxRequest? LastRequest { get; private set; }

        public Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxAvailability(
                kind,
                available,
                Verified: verified ?? available,
                available ? null : "测试：不可用",
                Detail: null));

        public Task<SandboxResult> RunAsync(
            SandboxRequest request,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastRequest = request;

            return Task.FromResult(new SandboxResult
            {
                JobId = request.JobId,
                Kind = kind,
                ExitCode = exitCode
            });
        }
    }
}
