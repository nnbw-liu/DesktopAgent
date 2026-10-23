using System.Runtime.CompilerServices;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 应用层接线：超预算自动压缩、失败兜底、关闭开关与手动压缩。</summary>
public sealed class AgentSessionRunnerCompactionTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
    };

    private static ChatSession BuildSession(int turns = 100, int charsPerMessage = 200)
    {
        var session = new ChatSession { Id = "s1", Title = "长会话" };

        for (var turn = 0; turn < turns; turn++)
        {
            session.Messages.Add(new ChatMessage(ChatMessageRole.User, $"问题 {turn}：" + new string('甲', charsPerMessage)));
            session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, $"回答 {turn}：" + new string('乙', charsPerMessage)));
        }

        return session;
    }

    private AgentSessionRunner CreateRunner(IAgentChatService chat, IContextSummarizer summarizer)
    {
        var builder = new ContextBuilder(_counter, _settings);
        var compactor = new ContextCompactor(
            _counter,
            summarizer,
            new ExtractiveContextSummarizer(_counter));
        return new AgentSessionRunner(chat, builder, _settings, compactor: compactor);
    }

    private AgentRunRequest Request(ChatSession session) => new(
        session.Messages.ToList(),
        Provider(),
        "m",
        new HashSet<string>(StringComparer.Ordinal),
        SessionId: session.Id,
        Session: session);

    [Fact]
    public async Task L1需要淘汰历史时自动压缩()
    {
        var session = BuildSession();
        var chat = new FakeChat();
        var runner = CreateRunner(chat, new FakeSummarizer());
        var telemetry = new ContextRunTelemetry();

        var text = string.Empty;

        await foreach (var delta in runner.StreamAsync(Request(session), telemetry))
        {
            text += delta.Text;
        }

        Assert.Equal(1, chat.Calls);
        Assert.Equal("ok", text);
        Assert.True(telemetry.Report!.SummaryUsed);
        Assert.True(telemetry.Report.CompactedMessages > 0);
        Assert.Equal(0, telemetry.Report.OmittedMessages);
        Assert.Contains("已压缩", telemetry.ContextNotice);
        Assert.True(session.SummaryThroughOrder > 0);
    }

    [Fact]
    public async Task 关闭自动压缩时只做硬裁剪()
    {
        _settings.Current.Context.AutoCompact = false;

        var session = BuildSession();
        var chat = new FakeChat();
        var runner = CreateRunner(chat, new FakeSummarizer());
        var telemetry = new ContextRunTelemetry();

        await foreach (var _ in runner.StreamAsync(Request(session), telemetry))
        {
        }

        Assert.Equal(1, chat.Calls);
        Assert.Equal(0, session.SummaryThroughOrder);
        Assert.Null(session.Summary);
        Assert.True(telemetry.Report!.OmittedMessages > 0);
        Assert.Null(telemetry.ContextNotice);
    }

    [Fact]
    public async Task 摘要失败时用兜底摘要且本轮仍能回答()
    {
        var session = BuildSession();
        var chat = new FakeChat();
        var runner = CreateRunner(chat, new FakeSummarizer(fail: true));
        var telemetry = new ContextRunTelemetry();

        await foreach (var _ in runner.StreamAsync(Request(session), telemetry))
        {
        }

        Assert.Equal(1, chat.Calls);
        Assert.True(telemetry.Report!.SummaryUsed);
        Assert.True(telemetry.Report.SummaryFallback);
        Assert.Contains("自动摘要", telemetry.ContextNotice);
        Assert.Contains(SummaryFormat.FallbackMarker, session.Summary);
    }

    [Fact]
    public async Task 未超预算时不调用摘要器()
    {
        var session = BuildSession(turns: 3, charsPerMessage: 20);
        var chat = new FakeChat();
        var summarizer = new FakeSummarizer();
        var runner = CreateRunner(chat, summarizer);
        var telemetry = new ContextRunTelemetry();

        await foreach (var _ in runner.StreamAsync(Request(session), telemetry))
        {
        }

        Assert.Equal(1, chat.Calls);
        Assert.Equal(0, summarizer.Calls);
        Assert.Null(session.Summary);
        Assert.False(telemetry.Report!.Trimmed);
    }

    [Fact]
    public void 预览只读估算且不触发压缩()
    {
        var session = BuildSession();
        var chat = new FakeChat();
        var runner = CreateRunner(chat, new FakeSummarizer());

        var plan = runner.Preview(Request(session));

        Assert.True(plan.Report.OmittedMessages > 0);
        Assert.False(plan.Report.SummaryUsed);
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, session.SummaryThroughOrder);
    }

    [Fact]
    public async Task 手动压缩使用强制模式()
    {
        var session = BuildSession(turns: 10, charsPerMessage: 2);
        _settings.Current.Context.KeepRecentTurns = 2;
        _settings.Current.Context.SmallModelKeepRecentTurns = 2;
        var chat = new FakeChat();
        var runner = CreateRunner(chat, new FakeSummarizer());

        var outcome = await runner.CompactAsync(new AgentCompactRequest(session, Provider(), "m", session.Id));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.Equal(16, session.SummaryThroughOrder);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task 没有压缩器时退回P1行为()
    {
        var session = BuildSession();
        var chat = new FakeChat();
        var builder = new ContextBuilder(_counter, _settings);
        var runner = new AgentSessionRunner(chat, builder, _settings);

        var telemetry = new ContextRunTelemetry();

        await foreach (var _ in runner.StreamAsync(Request(session), telemetry))
        {
        }

        Assert.Equal(1, chat.Calls);
        Assert.True(telemetry.Report!.OmittedMessages > 0);
        Assert.Equal(0, session.SummaryThroughOrder);
    }

    [Fact]
    public async Task L1放不下且没有可压缩内容时仍报需压缩()
    {
        var session = new ChatSession { Id = "s2", Title = "单条超长" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, new string('长', 50_000)));

        var chat = new FakeChat();
        var runner = CreateRunner(chat, new FakeSummarizer());

        var error = await Assert.ThrowsAsync<ContextBudgetException>(async () =>
        {
            await foreach (var _ in runner.StreamAsync(Request(session), new ContextRunTelemetry()))
            {
            }
        });

        Assert.Equal(AgentErrorKind.NeedsCompaction, error.Kind);
        Assert.Equal(0, chat.Calls);
    }

    private sealed class FakeChat : IAgentChatService
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<AgentDelta> RunAsync(
            AgentRunPlan plan,
            ContextRunTelemetry? telemetry = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new AgentDelta("ok", string.Empty);
        }
    }

    private static string ValidSummary() => """
        ## 目标与约束
        - 不要修改数据库 [m0]
        ## 已确认决策
        - 无
        ## 关键事实
        - 无
        ## 已改动
        - 无
        ## 未完成
        - 无
        ## 用户偏好
        - 无
        """;

    private sealed class FakeSummarizer : IContextSummarizer
    {
        private readonly bool _fail;

        public FakeSummarizer(bool fail = false) => _fail = fail;

        public int Calls { get; private set; }

        public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_fail
                ? SummaryResult.Failed("500")
                : new SummaryResult(ValidSummary(), true, 10, null));
        }
    }
}
