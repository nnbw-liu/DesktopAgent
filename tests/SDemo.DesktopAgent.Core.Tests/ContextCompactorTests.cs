using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 压缩编排：增量、滞回、pin 下界、保存原子性与兜底。</summary>
public sealed class ContextCompactorTests
{
    private readonly HeuristicTokenCounter _counter = new();

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m" }]
    };

    private static ChatSession BuildSession(int turns, int charsPerMessage = 200)
    {
        var session = new ChatSession { Id = "s1", Title = "压缩测试" };

        for (var turn = 0; turn < turns; turn++)
        {
            session.Messages.Add(new ChatMessage(ChatMessageRole.User, $"问题 {turn}：" + new string('甲', charsPerMessage)));
            session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, $"回答 {turn}：" + new string('乙', charsPerMessage)));
        }

        return session;
    }

    private static ContextSettings Settings(int inputMaxTokens = 6000, int summaryMaxTokens = 200) => new()
    {
        KeepRecentTurns = 2,
        SmallModelKeepRecentTurns = 2,
        SummaryMaxTokens = summaryMaxTokens,
        SummaryInputMaxTokens = inputMaxTokens,
        SummaryTimeoutSeconds = 1
    };

    private ContextCompactionRequest Request(ChatSession session, ContextSettings settings, bool force = false)
    {
        var profile = ModelProfilePresets.FromPreset("8K")!;
        return new ContextCompactionRequest(
            session,
            Provider(),
            "m",
            profile,
            settings,
            ContextBudget.Create(profile, settings),
            force);
    }

    private ContextCompactor CreateCompactor(FakeSummarizer summarizer, Func<ChatSession, CancellationToken, Task>? persist = null) =>
        new(_counter, summarizer, new ExtractiveContextSummarizer(_counter), persist);

    [Fact]
    public async Task 压缩推进覆盖范围且摘要按上限截断()
    {
        var session = BuildSession(20);
        var settings = Settings();
        var summarizer = new FakeSummarizer(SummaryOfLength(150));
        var compactor = CreateCompactor(summarizer);

        var outcome = await compactor.EnsureAsync(Request(session, settings));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.Equal(36, session.SummaryThroughOrder); // 20 轮保留最后 2 轮
        Assert.Equal(36, outcome.CompactedMessages);
        Assert.True(session.SummaryTokens <= settings.SummaryMaxTokens);
        Assert.False(string.IsNullOrWhiteSpace(session.Summary));
        Assert.Equal(1, summarizer.Calls);
      }

    [Fact]
    public async Task 第二次没有新增历史时不再调用模型()
    {
        var session = BuildSession(20);
        var settings = Settings();
        var summarizer = new FakeSummarizer(SummaryOfLength(120));
        var compactor = CreateCompactor(summarizer);

        await compactor.EnsureAsync(Request(session, settings));
        var second = await compactor.EnsureAsync(Request(session, settings));

        Assert.False(second.Compacted);
        Assert.Equal(1, summarizer.Calls);
    }

    [Fact]
    public async Task 新增不足四轮且省不下两百token时跳过()
    {
        var session = BuildSession(3, charsPerMessage: 2);
        var settings = Settings();
        var summarizer = new FakeSummarizer(SummaryOfLength(50));

        var outcome = await CreateCompactor(summarizer).EnsureAsync(Request(session, settings));

        Assert.False(outcome.Compacted);
        Assert.Equal(0, summarizer.Calls);
    }

    [Fact]
    public async Task 手动压缩忽略增量阈值()
    {
        var session = BuildSession(3, charsPerMessage: 2);
        var settings = Settings();
        var summarizer = new FakeSummarizer(SummaryOfLength(50));

        var outcome = await CreateCompactor(summarizer).EnsureAsync(Request(session, settings, force: true));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.Equal(1, summarizer.Calls);
    }

    [Fact]
    public async Task 保存失败时回滚内存摘要()
    {
        var session = BuildSession(20);
        var summaryBefore = session.Summary;
        var compactor = CreateCompactor(
            new FakeSummarizer(SummaryOfLength(100)),
            (_, _) => throw new IOException("磁盘只读"));

        var outcome = await compactor.EnsureAsync(Request(session, Settings()));

        Assert.False(outcome.Compacted);
        Assert.True(outcome.Failed);
        Assert.Equal(summaryBefore, session.Summary);
        Assert.Equal(0, session.SummaryThroughOrder);
        Assert.Contains("保存失败", outcome.Message);
    }

    [Fact]
    public async Task pin的消息阻止摘要越过它()
    {
        var session = BuildSession(10);
        session.Messages[4].IsPinned = true;

        var outcome = await CreateCompactor(new FakeSummarizer(SummaryOfLength(80)))
            .EnsureAsync(Request(session, Settings()));

        Assert.True(outcome.Compacted);
        Assert.Equal(4, session.SummaryThroughOrder);
    }

    [Fact]
    public async Task 模型摘要失败时使用确定性兜底()
    {
        var session = BuildSession(20);
        var summarizer = new FakeSummarizer(_ => SummaryResult.Failed("500"));

        var outcome = await CreateCompactor(summarizer).EnsureAsync(Request(session, Settings()));

        Assert.True(outcome.Compacted);
        Assert.True(outcome.UsedFallback);
        Assert.True(session.SummaryIsFallback);
        Assert.Contains(SummaryFormat.FallbackMarker, session.Summary);
    }

    [Fact]
    public async Task 超长历史按输入预算分块并携带上一版摘要()
    {
        var session = BuildSession(30, charsPerMessage: 400);
        var settings = Settings(inputMaxTokens: 600);
        var summarizer = new FakeSummarizer(SummaryOfLength(150));

        await CreateCompactor(summarizer).EnsureAsync(Request(session, settings));

        Assert.True(summarizer.Calls > 1, $"应当分块，实际调用 {summarizer.Calls} 次");
        Assert.NotNull(summarizer.Requests[1].PreviousSummary);
    }

    [Fact]
    public async Task 保存成功调用一次持久化()
    {
        var session = BuildSession(20);
        var saves = 0;
        var compactor = CreateCompactor(
            new FakeSummarizer(SummaryOfLength(80)),
            (_, _) =>
            {
                saves++;
                return Task.CompletedTask;
            });

        await compactor.EnsureAsync(Request(session, Settings()));

        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task 摘要token成本随压缩结果返回()
    {
        var session = BuildSession(20);
        var text = SummaryOfLength(150).Text;
        var summarizer = new FakeSummarizer(_ => new SummaryResult(
            text,
            UsedModel: true,
            Tokens: 0,
            Error: null,
            InputTokens: 900,
            OutputTokens: 120));

        var outcome = await CreateCompactor(summarizer).EnsureAsync(Request(session, Settings()));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.Equal(1, summarizer.Calls);
        Assert.Equal(900, outcome.SummaryInputTokens);
        Assert.Equal(120, outcome.SummaryOutputTokens);
    }

    private static SummaryResult SummaryOfLength(int length)
    {
        var body = string.Join(' ', Enumerable.Repeat("事实", Math.Max(1, length / 2)));
        var text = $"""
            ## 目标与约束
            - {body} [m1]
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

        return new SummaryResult(text, UsedModel: true, Tokens: 0, Error: null);
    }

    private sealed class FakeSummarizer : IContextSummarizer
    {
        private readonly Func<SummaryRequest, SummaryResult> _handler;

        public FakeSummarizer(SummaryResult result)
            : this(_ => result)
        {
        }

        public FakeSummarizer(string text)
            : this(new SummaryResult(text, true, 0, null))
        {
        }

        public FakeSummarizer(Func<SummaryRequest, SummaryResult> handler) => _handler = handler;

        public int Calls { get; private set; }

        public List<SummaryRequest> Requests { get; } = [];

        public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}
