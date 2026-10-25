using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>聊天室内的两个交互：按提供商分组的模型选择器、删除单条消息。</summary>
public sealed class ChatRoomInteractionTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomInteractionTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new QuietRunner(), _temp.Root);
    }

    [Fact]
    public void 模型选择按提供商分组并且可以收起()
    {
        _settings.Current.Providers.Clear();
        _settings.Current.Providers.Add(new ModelProvider
        {
            Id = "local",
            DisplayName = "本地",
            BaseUrl = "https://example.com/v1",
            ApiKey = "k",
            Models =
            [
                new ModelEntry { Id = "qwen3.5-4b", DisplayName = "Qwen 3.5 4B" },
                new ModelEntry { Id = "qwen3.5-8b", DisplayName = "Qwen 3.5 8B" }
            ]
        });
        _settings.Current.Providers.Add(new ModelProvider
        {
            Id = "cloud",
            DisplayName = "云端",
            BaseUrl = "https://example.com/v1",
            ApiKey = "k",
            Models = [new ModelEntry { Id = "deepseek-v3", DisplayName = "DeepSeek V3" }]
        });

        _settings.Current.LastProviderId = "local";
        _settings.Current.LastModelId = "qwen3.5-4b";

        var session = new ChatSession { Id = "models", Title = "模型" };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find(".model-picker-trigger").Click();

        Assert.Equal(2, cut.FindAll(".model-picker-group").Count);
        Assert.Equal(3, cut.FindAll(".model-picker-option:not(.model-picker-option--auto)").Count);
        Assert.Contains("本地", cut.FindAll(".model-picker-group-name").ElementAt(0).TextContent);
        Assert.Contains("云端", cut.FindAll(".model-picker-group-name").ElementAt(1).TextContent);

        // 收起第一组：只保留第二组的模型（「自动选择」始终在顶部）。
        cut.FindAll(".model-picker-group-head").ElementAt(0).Click();

        Assert.Single(cut.FindAll(".model-picker-group.is-collapsed"));
        Assert.Single(cut.FindAll(".model-picker-option:not(.model-picker-option--auto)"));
        Assert.Contains("DeepSeek V3", cut.Find(".model-picker-option:not(.model-picker-option--auto)").TextContent);

        // 选中后菜单关闭，按钮显示新的提供商 + 模型。
        cut.Find(".model-picker-option:not(.model-picker-option--auto)").Click();

        Assert.Empty(cut.FindAll(".model-picker-menu"));
        Assert.Contains("云端 · DeepSeek V3", cut.Find(".model-picker-current").TextContent);

        // 底部选择器写入当前聊天室，而不是只改全局「上次使用」。
        Assert.Equal("cloud", session.ScopedSettings.ProviderId);
        Assert.Equal("deepseek-v3", session.ScopedSettings.ModelId);
    }

    [Fact]
    public void 选择自动后清除当前聊天室的固定模型()
    {
        _settings.Current.ModelRouting.Mode = ModelRoutingMode.Rules;
        var session = new ChatSession
        {
            Id = "auto",
            Title = "自动",
            ScopedSettings = new ScopedSettings
            {
                ProviderId = "test",
                ModelId = "qwen3.5-4b",
                AutoModelRouting = false
            }
        };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find(".model-picker-trigger").Click();
        cut.Find(".model-picker-option--auto").Click();

        Assert.True(session.ScopedSettings.AutoModelRouting);
        Assert.Null(session.ScopedSettings.ProviderId);
        Assert.Null(session.ScopedSettings.ModelId);
        Assert.Contains("自动", cut.Find(".model-picker-current").TextContent);
    }

    [Fact]
    public void 聊天室未设置模型时继承工作区模型()
    {
        _settings.Current.Workspaces.Add(new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = _temp.Root,
            ScopedSettings = new ScopedSettings
            {
                ProviderId = "test",
                ModelId = "qwen3.5-4b"
            }
        });

        var session = new ChatSession { Id = "inherit", Title = "继承", WorkspaceId = "w1" };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Contains("本地 · qwen3.5-4b", cut.Find(".model-picker-current").TextContent);
    }

    [Fact]
    public void 占用环旁边显示聊天室累计输入输出token()
    {
        var session = new ChatSession { Id = "tokens", Title = "用量" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "你好"));
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "回答")
        {
            InputTokens = 1000,
            OutputTokens = 500
        });

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var total = cut.Find(".chat-token-total");
        Assert.Contains("1.5K", total.TextContent, StringComparison.Ordinal);
        Assert.Empty(total.QuerySelectorAll(".chat-token-breakdown"));
        Assert.Contains("输入 1.0K / 输出 500", total.GetAttribute("title")!, StringComparison.Ordinal);
    }

    [Fact]
    public void 删除消息需要确认并同步摘要边界()
    {
        var session = new ChatSession
        {
            Id = "delete",
            Title = "删除",
            Summary = "较早消息摘要",
            SummaryThroughOrder = 2,
            SummaryTokens = 10
        };

        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "第一条"));
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "第二条"));
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "第三条"));

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Equal(3, cut.FindAll(".msg-action--danger").Count);

        cut.FindAll(".msg-action--danger").ElementAt(0).Click();

        Assert.Contains("删除这条消息", cut.Find(".modal-title").TextContent);
        Assert.Equal(3, session.Messages.Count);

        cut.Find(".danger-button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, session.Messages.Count));
        Assert.DoesNotContain(session.Messages, message => message.Content == "第一条");
        Assert.Equal(1, session.SummaryThroughOrder);
        Assert.Empty(cut.FindAll(".modal"));
    }

    [Fact]
    public void 收起再展开提供商后模型重新出现()
    {
        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, new ChatSession { Id = "expand", Title = "展开" }));

        cut.Find(".model-picker-trigger").Click();
        cut.Find(".model-picker-group-head").Click();
        Assert.Empty(cut.FindAll(".model-picker-option:not(.model-picker-option--auto)"));

        cut.Find(".model-picker-group-head").Click();
        Assert.Single(cut.FindAll(".model-picker-option:not(.model-picker-option--auto)"));
    }

    /// <summary>只渲染：预览直接失败，不会真的请求模型。</summary>
    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("交互测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("交互测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("交互测试不压缩"));

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
