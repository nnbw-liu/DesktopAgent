using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Components.Settings;
using SDemo.DesktopAgent.Components.Sidebar;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>作用域设置：默认留空、感叹号继承信息、工作区菜单与聊天室入口。</summary>
public sealed class ScopeSettingsDrawerTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ScopeSettingsDrawerTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new QuietRunner(), _temp.Root);
    }

    [Fact]
    public void 聊天室设置默认留空并在感叹号里显示通用与工作区值()
    {
        var workspace = new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = _temp.Root,
            ScopedSettings = new ScopedSettings { BudgetRatio = 0.7 }
        };
        _settings.Current.Workspaces.Add(workspace);
        _settings.Current.Context.BudgetRatio = 0.85;

        var session = new ChatSession { Id = "c1", Title = "聊天", WorkspaceId = workspace.Id };
        var saves = 0;

        var cut = RenderComponent<ScopeSettingsDrawer>(parameters => parameters
            .Add(component => component.Kind, ScopeSettingsKind.Chat)
            .Add(component => component.Session, session)
            .Add(component => component.OnSessionChanged, _ => saves++));

        var row = cut.FindAll(".scoped-row")
            .First(element => element.TextContent.Contains("预算占窗口比例", StringComparison.Ordinal));
        var help = row.QuerySelector(".scoped-row-help")!.GetAttribute("title")!;
        var input = row.QuerySelector("input")!;

        Assert.Contains("通用设置：0.85", help, StringComparison.Ordinal);
        Assert.Contains("工作区设置：0.7", help, StringComparison.Ordinal);
        Assert.Equal(string.Empty, input.GetAttribute("value") ?? string.Empty);
        Assert.Null(session.ScopedSettings.BudgetRatio);

        var autoCompact = cut.FindAll(".scoped-row")
            .First(element => element.TextContent.Contains("自动压缩", StringComparison.Ordinal));
        autoCompact.QuerySelector("select")!.Change("false");

        Assert.False(session.ScopedSettings.AutoCompact);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void 工作区设置只显示通用上级值并提供工作区专用项()
    {
        var workspace = new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = _temp.Root,
            ScopedSettings = new ScopedSettings { BudgetRatio = 0.7 }
        };
        _settings.Current.Context.BudgetRatio = 0.85;

        var cut = RenderComponent<ScopeSettingsDrawer>(parameters => parameters
            .Add(component => component.Kind, ScopeSettingsKind.Workspace)
            .Add(component => component.Workspace, workspace));

        var row = cut.FindAll(".scoped-row")
            .First(element => element.TextContent.Contains("预算占窗口比例", StringComparison.Ordinal));
        var help = row.QuerySelector(".scoped-row-help")!.GetAttribute("title")!;

        Assert.Contains("通用设置：0.85", help, StringComparison.Ordinal);
        Assert.DoesNotContain("工作区设置", help, StringComparison.Ordinal);
        Assert.Contains("构建命令", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("归档会话", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void 工作区更多菜单能打开工作区设置()
    {
        var workspace = new Workspace { Id = "w1", Name = "工作区", Path = _temp.Root };
        Workspace? opened = null;

        var cut = RenderComponent<Sidebar>(parameters => parameters
            .Add(component => component.Workspaces, [workspace])
            .Add(component => component.OnOpenWorkspaceSettings, value => opened = value));

        cut.FindAll("button[title='更多']").First().Click();
        cut.FindAll(".item-menu-entry")
            .First(entry => entry.TextContent.Contains("工作区设置", StringComparison.Ordinal))
            .Click();

        Assert.Same(workspace, opened);
    }

    [Fact]
    public void 聊天室右上角设置按钮触发回调()
    {
        var session = new ChatSession { Id = "c1", Title = "聊天" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "你好"));
        var opened = false;

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.OnOpenSessionSettings, () => opened = true));

        cut.Find(".chat-settings-button").Click();

        Assert.True(opened);
    }

    [Fact]
    public void 空聊天室右上角也能打开设置()
    {
        var session = new ChatSession { Id = "empty", Title = "新聊天室" };
        var opened = false;

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.OnOpenSessionSettings, () => opened = true));

        cut.Find(".chat-empty-actions .chat-settings-button").Click();

        Assert.True(opened);
    }

    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("作用域设置测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("作用域设置测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("作用域设置测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
