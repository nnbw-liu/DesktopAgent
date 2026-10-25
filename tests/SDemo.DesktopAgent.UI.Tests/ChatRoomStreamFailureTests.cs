using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 流中途失败：错误也要作为「答案」的一步进时间线，否则按步骤拼接的答案会把它漏掉。
/// </summary>
public sealed class ChatRoomStreamFailureTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomStreamFailureTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new FailingRunner(), _temp.Root);
    }

    [Fact]
    public async Task 中途失败的错误出现在答案里()
    {
        var session = new ChatSession { Id = "stream-failure", Title = "中途失败" };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("看一下这个文件", [])));

        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);

        // 步骤里既有前半段正文，也有失败提示 —— 时间线按步骤拼答案，两条都不能丢。
        var answer = string.Concat(reply.Steps
            .Where(step => step.Kind == ChatStepKind.Text)
            .Select(step => step.Text));

        Assert.Contains("我先看一下", answer, StringComparison.Ordinal);
        Assert.Contains("调用失败", answer, StringComparison.Ordinal);
        Assert.Contains("调用失败", cut.Find(".timeline-answer").TextContent, StringComparison.Ordinal);
        Assert.Contains("我先看一下", cut.Find(".timeline-answer").TextContent, StringComparison.Ordinal);
    }

    /// <summary>先吐半句正文，然后抛异常。</summary>
    private sealed class FailingRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("失败测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("失败测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("失败测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new AgentDelta("我先看一下。", string.Empty);

            throw new InvalidOperationException("连接被断开");
        }
    }
}
