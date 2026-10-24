using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// P1b：本轮补工具要在界面上留一行（「为什么突然多了一个工具」）。
/// 宿主提示只进过程组、不进模型上下文。
/// </summary>
public sealed class ChatRoomToolExpansionTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomToolExpansionTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        // 完全展开档：过程步骤平铺渲染，提示行一定在 DOM 里。
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Expanded;
        ChatRoomFixture.Register(this, _settings, new ExpandingRunner(), _temp.Root);
    }

    [Fact]
    public async Task 补工具提示落成宿主提示步骤()
    {
        var session = new ChatSession { Id = "tool-expansion", Title = "补工具" };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("读一下这个 pdf", [])));

        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);

        // 步骤：宿主提示 → 正文。提示是过程步骤，答案仍然完整。
        var notice = Assert.Single(reply.Steps, step => step.Kind == ChatStepKind.HostPrompt);
        Assert.Equal("本轮补充工具：pdf_read", notice.Text);
        Assert.Equal(ChatGroupPart.Process, notice.Part);

        Assert.Contains("读完了", reply.Content, StringComparison.Ordinal);
        Assert.Contains("本轮补充工具：pdf_read", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("宿主提示", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>第一步补工具（只发提示、不发正文），第二步收尾。</summary>
    private sealed class ExpandingRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("补工具测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("补工具测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("补工具测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new AgentDelta(
                string.Empty,
                string.Empty,
                Step: 0,
                Notice: "本轮补充工具：pdf_read");

            yield return new AgentDelta("读完了。", string.Empty, Step: 1);
        }
    }
}
