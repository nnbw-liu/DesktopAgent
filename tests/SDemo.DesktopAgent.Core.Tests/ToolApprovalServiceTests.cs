using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>S3 通用审批：串行、取消按拒绝、CancelAll 清空排队请求。</summary>
public sealed class ToolApprovalServiceTests
{
    [Fact]
    public async Task 请求串行展示且各自独立批准()
    {
        var service = new ToolApprovalService();

        var first = service.RequestAsync(Request("first"));
        await WaitUntilAsync(() => service.Current?.ToolId == "first");

        var second = service.RequestAsync(Request("second"));

        // 第二个请求排队，不能抢走界面上的第一个。
        await Task.Delay(50);
        Assert.Equal("first", service.Current!.ToolId);

        service.Resolve(ToolApprovalDecision.ApproveOnce);
        Assert.Equal(ToolApprovalDecision.ApproveOnce, await first);

        await WaitUntilAsync(() => service.Current?.ToolId == "second");
        service.Resolve(ToolApprovalDecision.Deny);
        Assert.Equal(ToolApprovalDecision.Deny, await second);

        await WaitUntilAsync(() => service.Current is null);
    }

    [Fact]
    public async Task 取消等待中的请求按拒绝处理()
    {
        var service = new ToolApprovalService();
        using var cts = new CancellationTokenSource();

        var request = service.RequestAsync(Request("script"), cts.Token);
        await WaitUntilAsync(() => service.Current?.ToolId == "script");

        cts.Cancel();

        Assert.Equal(ToolApprovalDecision.Deny, await request);
        await WaitUntilAsync(() => service.Current is null);
    }

    [Fact]
    public async Task CancelAll拒绝当前与排队请求()
    {
        var service = new ToolApprovalService();

        var first = service.RequestAsync(Request("first"));
        await WaitUntilAsync(() => service.Current?.ToolId == "first");

        var second = service.RequestAsync(Request("second"));

        service.CancelAll();

        Assert.Equal(ToolApprovalDecision.Deny, await first);
        Assert.Equal(ToolApprovalDecision.Deny, await second);
        await WaitUntilAsync(() => service.Current is null);
    }

    [Fact]
    public async Task 没有待确认项时Resolve不抛异常()
    {
        var service = new ToolApprovalService();

        service.Resolve(ToolApprovalDecision.ApproveOnce);
        service.CancelAll();

        await Task.Yield();
        Assert.Null(service.Current);
    }

    private static ToolApprovalRequest Request(string toolId) => new(
        toolId,
        ToolApprovalKind.ProcessExecution,
        "运行 PowerShell 脚本",
        "沙箱：AppContainer",
        "Write-Output ok",
        AgentToolRisk.High,
        AllowSessionRemember: false,
        SessionKey: null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("等待条件超时。");
    }
}
