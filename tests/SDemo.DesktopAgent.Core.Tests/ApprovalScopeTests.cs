using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>R1.4：只有真正上屏的审批才计入本轮 metrics；自动审批 / 无作用域不计。</summary>
public sealed class ApprovalScopeTests
{
    [Fact]
    public async Task 审批上屏时计入本轮metrics()
    {
        var metrics = new AgentRunMetrics();
        var service = new ToolApprovalService();

        using (ApprovalScope.Begin(metrics))
        {
            var request = service.RequestAsync(Request("edit"));
            await WaitUntilAsync(() => service.Current is not null);
            service.Resolve(ToolApprovalDecision.ApproveOnce);

            Assert.Equal(ToolApprovalDecision.ApproveOnce, await request);
        }

        Assert.Equal(1, metrics.ApprovalCount);
    }

    [Fact]
    public async Task 没有作用域时不计入任何metrics()
    {
        var service = new ToolApprovalService();

        var request = service.RequestAsync(Request("edit"));
        await WaitUntilAsync(() => service.Current is not null);
        service.Resolve(ToolApprovalDecision.Deny);

        Assert.Equal(ToolApprovalDecision.Deny, await request);
    }

    [Fact]
    public async Task 作用域结束后不再计数()
    {
        var metrics = new AgentRunMetrics();
        var service = new ToolApprovalService();

        using (ApprovalScope.Begin(metrics))
        {
            // 作用域内先演示一次，然后退出。
        }

        var request = service.RequestAsync(Request("edit"));
        await WaitUntilAsync(() => service.Current is not null);
        service.Resolve(ToolApprovalDecision.Deny);
        await request;

        Assert.Equal(0, metrics.ApprovalCount);
        Assert.Equal(0, metrics.RejectedToolCalls);
    }

    [Fact]
    public void 未开启作用域时记录是空操作()
    {
        ApprovalScope.RecordPresented();
    }

    private static ToolApprovalRequest Request(string toolId) => new(
        toolId,
        ToolApprovalKind.FileEdit,
        "写入文件",
        "/tmp/a.txt",
        "+one",
        AgentToolRisk.Medium,
        AllowSessionRemember: true,
        SessionKey: "s1");

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
