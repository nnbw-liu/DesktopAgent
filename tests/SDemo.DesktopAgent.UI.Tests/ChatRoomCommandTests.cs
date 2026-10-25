using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// `/命令` 在聊天室里真正执行：命令不进模型、不产生消息；未知命令只提示。
/// </summary>
public sealed class ChatRoomCommandTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomCommandTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new QuietRunner(), _temp.Root);
    }

    [Fact]
    public async Task 未知命令只提示不发送()
    {
        var cut = RenderRoom();

        await RunCommandAsync(cut, "/nope");

        Assert.Contains("未知命令", cut.Find(".context-notice-text").TextContent, StringComparison.Ordinal);
        // 命令不产生消息：只有预置的那两条。
        Assert.Equal(2, cut.Instance.Session.Messages.Count);
    }

    [Fact]
    public async Task 压缩命令走压缩服务并把结果告诉用户()
    {
        var cut = RenderRoom();

        await RunCommandAsync(cut, "/compact");

        // QuietRunner 的 CompactAsync 固定回「呈现测试不压缩」：证明命令确实打到了服务上。
        Assert.Contains("呈现测试不压缩", cut.Find(".context-notice-text").TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.Instance.Session.Messages.Count);
    }

    [Fact]
    public async Task 帮助命令列出可用命令()
    {
        var cut = RenderRoom();

        await RunCommandAsync(cut, "/help");

        var notice = cut.Find(".context-notice-text").TextContent;
        Assert.Contains("/compact", notice, StringComparison.Ordinal);
        Assert.Contains("/model", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有工作区时重建索引给明确提示()
    {
        var cut = RenderRoom();

        await RunCommandAsync(cut, "/index");

        Assert.Contains("还没有归属工作区", cut.Find(".context-notice-text").TextContent, StringComparison.Ordinal);
    }

    private IRenderedComponent<ChatRoom> RenderRoom()
    {
        // 空会话里提示条不渲染：先放一轮对话，让命令反馈有地方显示。
        var session = new ChatSession { Id = "commands-room", Title = "命令" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "你好"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "在的。"));

        return RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));
    }

    /// <summary>把整行打进输入框，再触发 Enter（等价于用户在菜单关闭时按回车）。</summary>
    private static async Task RunCommandAsync(IRenderedComponent<ChatRoom> cut, string line)
    {
        cut.Find("textarea").Input(line);

        var composer = cut.FindComponent<ChatComposer>();
        await cut.InvokeAsync(() => composer.Instance.HandleEnterAsync());
    }

    /// <summary>什么都不生成的 Runner：命令测试只关心「有没有打到服务上」。</summary>
    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("命令测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("命令测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("呈现测试不压缩"));

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

