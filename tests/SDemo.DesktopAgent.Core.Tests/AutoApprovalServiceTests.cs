using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 「完全权限」的审批门：直接批准、不进界面队列；已经取消的调用仍然拒绝。
/// </summary>
public sealed class AutoApprovalServiceTests
{
    [Fact]
    public async Task 直接批准且不占用界面上的待确认项()
    {
        var inner = new ToolApprovalService();
        var gate = new AutoApprovalService(inner);

        var changed = 0;
        gate.Changed += () => changed++;

        var decision = await gate.RequestAsync(Request("write_file"));

        Assert.Equal(ToolApprovalDecision.ApproveOnce, decision);

        // 没有排队展示，所以界面不会出现确认框。
        Assert.Null(inner.Current);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task 已经取消的调用按拒绝处理()
    {
        var gate = new AutoApprovalService(new ToolApprovalService());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Equal(ToolApprovalDecision.Deny, await gate.RequestAsync(Request("script"), cts.Token));
    }

    [Fact]
    public async Task 高风险工具也不会拿到会话级批准()
    {
        var gate = new AutoApprovalService(new ToolApprovalService());

        // 只批准一次：后续每次调用都要重新过这道门，不会变成隐式的会话授权。
        Assert.Equal(ToolApprovalDecision.ApproveOnce, await gate.RequestAsync(Request("run_powershell")));
    }

    private static ToolApprovalRequest Request(string toolId) => new(
        ToolId: toolId,
        Kind: ToolApprovalKind.ProcessExecution,
        Title: "执行",
        Summary: "测试",
        Detail: "echo hi",
        Risk: AgentToolRisk.High,
        AllowSessionRemember: false,
        SessionKey: null);
}
