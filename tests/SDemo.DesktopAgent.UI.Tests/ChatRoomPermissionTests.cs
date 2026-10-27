using System.Runtime.CompilerServices;
using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 输入框里的聊天室级权限只写本聊天室的作用域：
/// 完全权限 = 跳过确认 + 关闭沙箱；单独关沙箱不改变审批设置；再点一次恢复继承。
/// </summary>
public sealed class ChatRoomPermissionTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomPermissionTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new QuietRunner(), _temp.Root);
    }

    [Fact]
    public void 打开完全权限同时关闭本聊天室的沙箱()
    {
        var session = new ChatSession { Id = "chat", Title = "权限" };
        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        cut.InvokeAsync(() => cut.FindAll(".composer-actions button.composer-tool").ToArray()[0].Click());

        Assert.True(session.ScopedSettings.SkipApprovals);
        Assert.False(session.ScopedSettings.SandboxEnabled);

        // 再点一次回到标准权限：沙箱开关交回继承，而不是写死成「开」。
        cut.WaitForAssertion(() =>
            Assert.Contains("is-on", cut.FindAll(".composer-actions button.composer-tool").ToArray()[0].ClassList));

        cut.InvokeAsync(() => cut.FindAll(".composer-actions button.composer-tool").ToArray()[0].Click());

        Assert.Null(session.ScopedSettings.SkipApprovals);
        Assert.Null(session.ScopedSettings.SandboxEnabled);
    }

    [Fact]
    public void 单独关沙箱不影响审批设置()
    {
        var session = new ChatSession { Id = "chat", Title = "沙箱" };
        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var sandbox = cut.FindAll(".composer-actions button.composer-tool").ToArray()[1];
        Assert.Equal("沙箱", sandbox.GetAttribute("aria-label"));

        cut.InvokeAsync(() => sandbox.Click());

        Assert.False(session.ScopedSettings.SandboxEnabled);
        Assert.Null(session.ScopedSettings.SkipApprovals);

        // 沙箱按钮变成关闭态，输入框里能看到。
        cut.WaitForAssertion(() =>
            Assert.Contains("is-off", cut.FindAll(".composer-actions button.composer-tool").ToArray()[1].ClassList));
    }

    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("权限测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("权限测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("权限测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
