using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Settings;
using SDemo.DesktopAgent.Components.Sidebar;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>用量弹窗、侧栏入口与会话分页。</summary>
public sealed class UsageAndSidebarTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public UsageAndSidebarTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new QuietRunner(), _temp.Root);
    }

    [Fact]
    public async Task 用量弹窗显示总输入输出token()
    {
        var store = Services.GetRequiredService<ChatStore>();
        var session = new ChatSession { Id = "usage", Title = "用量", UpdatedAt = DateTime.Now };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "回答")
        {
            ProviderId = "test",
            ModelId = "qwen3.5-4b",
            InputTokens = 1000,
            OutputTokens = 500
        });

        await store.SaveSessionAsync(session);

        var cut = RenderComponent<UsageCostDialog>();

        cut.WaitForAssertion(() => Assert.Contains("用量与成本", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("1.0K", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("500", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void 侧栏用量入口在设置上方并能打开弹窗()
    {
        var opened = false;

        var cut = RenderComponent<Sidebar>(parameters => parameters
            .Add(component => component.OnOpenUsage, () => opened = true));

        var usageButton = cut.FindAll("button")
            .First(button => button.TextContent.Contains("用量与成本", StringComparison.Ordinal));
        usageButton.Click();

        Assert.True(opened);
    }

    [Fact]
    public void 会话过多时折叠并可展开其余对话()
    {
        var sessions = Enumerable.Range(0, 8)
            .Select(index => new ChatSession
            {
                Id = $"s{index}",
                Title = $"会话 {index}",
                UpdatedAt = DateTime.Now.AddMinutes(-index)
            })
            .ToList();

        var cut = RenderComponent<Sidebar>(parameters => parameters
            .Add(component => component.Sessions, sessions));

        Assert.Equal(6, cut.FindAll(".session-row").Count);
        var more = cut.Find(".session-show-more");
        Assert.Contains("展开其余 2 个对话", more.TextContent, StringComparison.Ordinal);

        more.Click();

        Assert.Equal(8, cut.FindAll(".session-row").Count);
        Assert.Contains("收起对话", cut.Find(".session-show-more").TextContent, StringComparison.Ordinal);
    }

    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("用量测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("用量测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("用量测试不压缩"));

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
