using System.Diagnostics;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>直跑执行器：退出码、超时、取消、输出上限、环境变量白名单。</summary>
public sealed class DirectProcessRunnerTests
{
    private static readonly string Dotnet = DirectProcessRunner.ResolveExecutable("dotnet")
        ?? throw new InvalidOperationException("测试机没有 dotnet。");

    [Fact]
    public async Task 正常退出带输出()
    {
        using var temp = new P4TempDirectory();

        var result = await new DirectProcessRunner().RunAsync(
            Dotnet,
            ["--version"],
            temp.Root,
            TimeSpan.FromSeconds(30),
            4096);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains('.', result.StdOut);
    }

    [Fact]
    public async Task 非零退出保留退出码()
    {
        using var temp = new P4TempDirectory();

        var result = await new DirectProcessRunner().RunAsync(
            Dotnet,
            ["--definitely-not-a-real-option"],
            temp.Root,
            TimeSpan.FromSeconds(30),
            4096);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task 超时终止进程树()
    {
        using var temp = new P4TempDirectory();
        var cmd = DirectProcessRunner.ResolveExecutable("cmd");
        Assert.NotNull(cmd);
        var stopwatch = Stopwatch.StartNew();

        var result = await new DirectProcessRunner().RunAsync(
            cmd!,
            ["/c", "ping", "-n", "10", "127.0.0.1"],
            temp.Root,
            TimeSpan.FromMilliseconds(300),
            4096);

        stopwatch.Stop();
        Assert.True(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task 取消终止进程树()
    {
        using var temp = new P4TempDirectory();
        var cmd = DirectProcessRunner.ResolveExecutable("cmd");
        Assert.NotNull(cmd);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DirectProcessRunner().RunAsync(
                cmd!,
                ["/c", "ping", "-n", "10", "127.0.0.1"],
                temp.Root,
                TimeSpan.FromSeconds(30),
                4096,
                cancellation.Token));
    }

    [Fact]
    public async Task 输出超过上限时截断()
    {
        using var temp = new P4TempDirectory();

        var result = await new DirectProcessRunner().RunAsync(
            Dotnet,
            ["--info"],
            temp.Root,
            TimeSpan.FromSeconds(30),
            256);

        Assert.True(result.StdOutTruncated || result.StdErrTruncated);
    }

    [Fact]
    public async Task 环境变量白名单不泄漏自定义变量()
    {
        using var temp = new P4TempDirectory();
        var cmd = DirectProcessRunner.ResolveExecutable("cmd");
        Assert.NotNull(cmd);
        Environment.SetEnvironmentVariable("P4_SECRET_FOR_TEST", "visible");

        try
        {
            var result = await new DirectProcessRunner().RunAsync(
                cmd!,
                ["/c", "echo", "%P4_SECRET_FOR_TEST%"],
                temp.Root,
                TimeSpan.FromSeconds(10),
                4096);

            Assert.DoesNotContain("visible", result.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable("P4_SECRET_FOR_TEST", null);
        }
    }
}
