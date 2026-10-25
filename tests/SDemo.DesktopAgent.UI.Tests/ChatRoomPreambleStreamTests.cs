using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 流式过程中的边界：工具调用出现时，它前面已经流出来的正文要立即改判成过程正文，
/// 而不是等消息结束才移动；最终答案始终留在过程组外。
/// </summary>
public sealed class ChatRoomPreambleStreamTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomPreambleStreamTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Standard;
        ChatRoomFixture.Register(this, _settings, new PreambleRunner(), _temp.Root);
    }

    [Fact]
    public async Task 工具出现前的流式正文会变成过程正文()
    {
        var session = new ChatSession { Id = "stream-preamble", Title = "过程正文" };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("看看 README", [])));

        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);

        Assert.Equal(
            [ChatStepKind.Preamble, ChatStepKind.Tool, ChatStepKind.Text],
            reply.Steps.Select(step => step.Kind));

        // 收起时只看到最终答案；「我先读一下」已经随过程组一起折叠。
        var answer = cut.Find(".msg-body--markdown").TextContent;
        Assert.Contains("读完了，结论在这里。", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("我先读一下", answer, StringComparison.Ordinal);

        cut.Find(".process-head").Click();
        Assert.Contains("我先读一下 README。", cut.Find(".timeline-step--preamble").TextContent, StringComparison.Ordinal);
    }

    /// <summary>模拟真实服务：正文 → 工具轨迹 → 空增量（让 UI 同步轨迹）→ 最终正文。</summary>
    private sealed class PreambleRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("过程正文测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("过程正文测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("过程正文测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new AgentDelta("我先读一下 README。", string.Empty, Step: 0);

            request.Metrics?.RecordToolTrace(new ToolTraceEntry(
                "read_file",
                ToolTraceEntry.StatusOk,
                null,
                "README.md",
                "读取 120 行",
                3200,
                DateTime.Now,
                ReasoningChars: 0,
                TextChars: 9,
                CallId: "call-1"));

            // 真实服务在工具事件后先发一个（可能没有正文的）增量，UI 借此同步工具轨迹。
            yield return new AgentDelta(string.Empty, string.Empty, Step: 0);

            yield return new AgentDelta("读完了，结论在这里。", string.Empty, Step: 1);
        }
    }
}
