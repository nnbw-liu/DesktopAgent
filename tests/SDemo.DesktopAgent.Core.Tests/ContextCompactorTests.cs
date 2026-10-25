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

    private ContextCompactor CreateCompactor(IContextSummarizer summarizer, Func<ChatSession, CancellationToken, Task>? persist = null) =>
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
        // 手动压缩不看「新增不足 4 轮」的阈值；但摘要仍必须比被替换的内容小（P2b）。
        var session = BuildSession(3, charsPerMessage: 400);
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
    public async Task 开始与提交各落库一次_中间态只有开始标记()
    {
        var session = BuildSession(20);
        var states = new List<(DateTime? Started, DateTime? Finished, string? Summary, int Through)>();
        var compactor = CreateCompactor(
            new FakeSummarizer(SummaryOfLength(80)),
            (target, _) =>
            {
                states.Add((
                    target.CompactionStartedAt,
                    target.CompactionFinishedAt,
                    target.Summary,
                    target.SummaryThroughOrder));
                return Task.CompletedTask;
            });

        var outcome = await compactor.EnsureAsync(Request(session, Settings()));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.Equal(2, states.Count);

        // 第一次落库是事务开始：只有开始标记，摘要与覆盖范围都还没动。
        Assert.NotNull(states[0].Started);
        Assert.Null(states[0].Finished);
        Assert.Equal(0, states[0].Through);

        // 第二次落库：摘要是「开始之后」一次性提交的，同时收尾。
        Assert.NotNull(states[1].Finished);
        Assert.NotNull(states[1].Summary);
        Assert.Equal(36, states[1].Through);
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

    // ---------- P2b §3.3.2 第三条：事务化 + 拒绝不缩小 ----------

    [Fact]
    public async Task 摘要没有缩小时被拒绝并保留原文()
    {
        // 3 轮各 2 字符（约 18 token），摘要却有 77 token：换进去只会更大，直接拒绝。
        var session = BuildSession(3, charsPerMessage: 2);
        var summarizer = new FakeSummarizer(SummaryOfLength(50));

        var outcome = await CreateCompactor(summarizer)
            .EnsureAsync(Request(session, Settings(), force: true));

        Assert.False(outcome.Compacted);
        Assert.True(outcome.Rejected);
        Assert.Contains("没有缩小", outcome.Message);
        Assert.Null(session.Summary);
        Assert.Equal(0, session.SummaryThroughOrder);
        Assert.Equal(ContextCompactor.ReasonRejected, session.CompactionReason);
        Assert.False(session.HasUnfinishedCompaction);
    }

    [Fact]
    public async Task 已有未收尾压缩时让路_不重复压同一段()
    {
        var session = BuildSession(20);
        session.CompactionStartedAt = DateTime.Now.AddSeconds(-3);
        session.CompactionFinishedAt = null;

        var summarizer = new FakeSummarizer(SummaryOfLength(80));
        var outcome = await CreateCompactor(summarizer).EnsureAsync(Request(session, Settings()));

        Assert.False(outcome.Compacted);
        Assert.True(outcome.Busy);
        Assert.Contains("还没收尾", outcome.Message);
        Assert.Equal(0, summarizer.Calls);
        Assert.Equal(0, session.SummaryThroughOrder);
    }

    [Fact]
    public async Task 崩溃后读回的未完成标记由下一次压缩接管()
    {
        var session = BuildSession(20);
        session.CompactionStartedAt = DateTime.Now.AddMinutes(-30);
        session.CompactionFinishedAt = null;
        session.CompactionInterruptedOnLoad = true;

        var outcome = await CreateCompactor(new FakeSummarizer(SummaryOfLength(80)))
            .EnsureAsync(Request(session, Settings()));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.Contains("上次压缩未完成", outcome.Message);
        Assert.False(session.HasUnfinishedCompaction);
        Assert.False(session.CompactionInterruptedOnLoad);
    }

    [Fact]
    public async Task 提交前崩溃时库里只有开始标记没有半截摘要()
    {
        var session = BuildSession(20);
        var crashStates = new List<(DateTime? Started, DateTime? Finished, string? Summary)>();

        var compactor = CreateCompactor(
            new FakeSummarizer(SummaryOfLength(80)),
            (target, _) =>
            {
                if (crashStates.Count == 1)
                {
                    // 第二次落库（提交）时进程崩了。
                    throw new IOException("断电");
                }

                crashStates.Add((target.CompactionStartedAt, target.CompactionFinishedAt, target.Summary));
                return Task.CompletedTask;
            });

        var outcome = await compactor.EnsureAsync(Request(session, Settings()));

        Assert.False(outcome.Compacted);
        Assert.True(outcome.Failed);

        // 库里留下的是「只有开始」：没有摘要，覆盖范围也没动 —— 下次打开显示未完成，并重做这一段。
        var persisted = Assert.Single(crashStates);
        Assert.NotNull(persisted.Started);
        Assert.Null(persisted.Finished);
        Assert.Null(persisted.Summary);
        Assert.Equal(0, session.SummaryThroughOrder);
    }

    [Fact]
    public async Task 提交保存失败后不会被当成有压缩在跑()
    {
        var session = BuildSession(20);
        var calls = 0;

        var compactor = CreateCompactor(
            new FakeSummarizer(SummaryOfLength(80)),
            (_, _) =>
            {
                // 第 2 次落库（提交）失败，之后的收尾写入正常。
                if (++calls == 2)
                {
                    throw new IOException("磁盘只读");
                }

                return Task.CompletedTask;
            });

        var first = await compactor.EnsureAsync(Request(session, Settings()));

        Assert.False(first.Compacted);
        Assert.True(first.Failed);

        // 内存里的标记必须已经收尾：否则这个会话在本进程里永远「有压缩在跑」。
        Assert.False(session.HasUnfinishedCompaction);

        var second = await compactor.EnsureAsync(Request(session, Settings()));

        Assert.False(second.Busy);
        Assert.True(second.Compacted, second.Message);
    }

    [Fact]
    public async Task 摘要器抛出异常时事务也会收尾()
    {
        var session = BuildSession(20);
        var compactor = CreateCompactor(new ThrowingSummarizer());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await compactor.EnsureAsync(Request(session, Settings())));

        // 不收尾的话，这个会话会永远停在「有压缩在跑」，之后每一轮都让路。
        Assert.False(session.HasUnfinishedCompaction);
        Assert.StartsWith("failed:", session.CompactionReason);
    }

    [Fact]
    public async Task 宿主上下文快照不进入压缩范围()
    {
        var session = BuildSession(20);
        // 快照是一条 user 消息（P2a 的传输形态），但它不是一轮对话、也不许被摘要吃掉。
        const int snapshotIndex = 30;
        session.Messages.Insert(snapshotIndex, new ChatMessage(
            ChatMessageRole.User,
            Context.HostSnapshot.Compose("### 时间\n2026-10-25T12:00:00+08:00")));

        var outcome = await CreateCompactor(new FakeSummarizer(SummaryOfLength(80)))
            .EnsureAsync(Request(session, Settings()));

        Assert.True(outcome.Compacted, outcome.Message);
        Assert.True(session.SummaryThroughOrder <= snapshotIndex, $"覆盖到 {session.SummaryThroughOrder}");
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

    private sealed class ThrowingSummarizer : IContextSummarizer
    {
        public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("摘要器炸了");
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
