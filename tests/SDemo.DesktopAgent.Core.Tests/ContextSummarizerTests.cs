using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 摘要器：模型失败 / 超时 / 格式不符都返回失败，兜底永远成功。</summary>
public sealed class ContextSummarizerTests
{
    private readonly HeuristicTokenCounter _counter = new();

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m" }]
    };

    private static List<ChatMessage> Chunk() =>
    [
        new(ChatMessageRole.User, "不要修改数据库结构"),
        new(ChatMessageRole.Assistant, "好的"),
        new(ChatMessageRole.User, "把改动都放在 docs/ 目录")
    ];

    private SummaryRequest Request(TimeSpan? timeout = null) => new(
        PreviousSummary: null,
        Chunk: Chunk(),
        ChunkStartOrder: 0,
        Provider(),
        "m",
        MaxTokens: 400,
        InputMaxTokens: 6000,
        Timeout: timeout ?? TimeSpan.FromSeconds(5));

    private static string ValidSummary() => """
        ## 目标与约束
        - 不要修改数据库结构 [m0]
        ## 已确认决策
        - 改动放 docs/ [m2]
        ## 关键事实
        - 无
        ## 已改动
        - 无
        ## 未完成
        - 无
        ## 用户偏好
        - 无
        """;

    [Fact]
    public async Task 模型返回合规摘要时采用()
    {
        var summarizer = new LlmContextSummarizer(new FakeTransport(ValidSummary()), _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.UsedModel);
        Assert.Contains("不要修改数据库结构", result.Text);
    }

    [Fact]
    public async Task 模型返回格式不符时降级()
    {
        var summarizer = new LlmContextSummarizer(
            new FakeTransport("这是一段自由格式的总结，没有小节。"), _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.UsedModel);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task 摘要超时返回失败而不是抛出()
    {
        var summarizer = new LlmContextSummarizer(new FakeTransport(delay: TimeSpan.FromSeconds(5)), _counter);

        var result = await summarizer.SummarizeAsync(
            Request(TimeSpan.FromMilliseconds(50)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("超时", result.Error);
    }

    [Fact]
    public async Task 传输异常返回失败()
    {
        var summarizer = new LlmContextSummarizer(new FakeTransport(exception: new HttpRequestException("500")), _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task 兜底摘要包含用户原话与来源且永远成功()
    {
        var summarizer = new ExtractiveContextSummarizer(_counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.False(result.Ok); // 兜底不标记 UsedModel
        Assert.Contains(SummaryFormat.FallbackMarker, result.Text);
        Assert.Contains("不要修改数据库结构", result.Text);
        Assert.Contains("[m0]", result.Text);
        Assert.True(result.Tokens > 0);
    }

    [Fact]
    public async Task 兜底摘要保留上一版摘要的线索()
    {
        var summarizer = new ExtractiveContextSummarizer(_counter);
        var request = Request() with { PreviousSummary = "## 目标与约束\n- 早期约束：不要动数据库 [m1]" };

        var result = await summarizer.SummarizeAsync(request, CancellationToken.None);

        Assert.Contains("此前摘要", result.Text);
        Assert.Contains("不要动数据库", result.Text);
    }

    [Fact]
    public async Task 兜底摘要去重写入路径且忽略非路径参数()
    {
        var chunk = new List<ChatMessage>
        {
            new(ChatMessageRole.User, "改两个文件"),
            new(ChatMessageRole.Assistant, "完成")
            {
                ToolTrace =
                [
                    new ToolTraceEntry("write_file", ToolTraceEntry.StatusOk, null, "src/a.cs", "ok", 10, DateTime.Now),
                    new ToolTraceEntry("read_file", ToolTraceEntry.StatusOk, null, "src/b.cs", "ok", 10, DateTime.Now),
                    new ToolTraceEntry("write_file", ToolTraceEntry.StatusOk, null, "src/a.cs", "ok", 10, DateTime.Now),
                    new ToolTraceEntry("apply_patch", ToolTraceEntry.StatusOk, null, "sha256:1234（80 字符）", "ok", 10, DateTime.Now)
                ]
            }
        };

        var summarizer = new ExtractiveContextSummarizer(_counter);
        var result = await summarizer.SummarizeAsync(
            Request() with { Chunk = chunk, ChunkStartOrder = 10 }, CancellationToken.None);

        var editsSection = result.Text.Split("## 已改动")[1];

        Assert.Contains("src/a.cs", editsSection);
        Assert.DoesNotContain("src/b.cs", editsSection);
        Assert.DoesNotContain("sha256:", editsSection);
    }

    [Fact]
    public async Task 模型格式不符时重试一次并采用第二次结果()
    {
        var transport = new SequenceTransport(
            "只有一句话，没有小节。",
            ValidSummary());
        var summarizer = new LlmContextSummarizer(transport, _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task 重试仍格式不符时降级()
    {
        var transport = new SequenceTransport("没有小节", "还是没有小节");
        var summarizer = new LlmContextSummarizer(transport, _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task 兜底摘要保留最早的用户约束()
    {
        var chunk = new List<ChatMessage>();
        chunk.Add(new ChatMessage(ChatMessageRole.User, "约束：新文件只能放 src/ 下"));
        chunk.Add(new ChatMessage(ChatMessageRole.Assistant, "记住了"));

        for (var index = 0; index < 12; index++)
        {
            chunk.Add(new ChatMessage(ChatMessageRole.User, $"第 {index} 轮填充内容"));
            chunk.Add(new ChatMessage(ChatMessageRole.Assistant, "处理完成"));
        }

        var summarizer = new ExtractiveContextSummarizer(_counter);
        var result = await summarizer.SummarizeAsync(
            Request() with { Chunk = chunk, ChunkStartOrder = 0 }, CancellationToken.None);

        Assert.Contains("新文件只能放 src/ 下", result.Text);
    }

    [Fact]
    public async Task 模型摘要记录输入输出token成本()
    {
        var summarizer = new LlmContextSummarizer(new FakeTransport(ValidSummary()), _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.InputTokens > 0, "输入 token 必须大于 0");
        Assert.True(result.OutputTokens > 0, "输出 token 必须大于 0");
        Assert.Equal(_counter.Count(result.Text), result.OutputTokens);
    }

    [Fact]
    public async Task 格式重试时两次请求都计入输入成本()
    {
        var transport = new SequenceTransport("自由格式，没有小节。", ValidSummary());
        var summarizer = new LlmContextSummarizer(transport, _counter);

        var result = await summarizer.SummarizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, transport.Calls);

        // 第二次提示多了一段格式纠正，输入成本必须严格大于单次请求。
        var single = await new LlmContextSummarizer(new FakeTransport(ValidSummary()), _counter)
            .SummarizeAsync(Request(), CancellationToken.None);

        Assert.True(result.InputTokens > single.InputTokens);
    }

    // ---------- P2b §3.3.2 第二条：摘要请求复用热前缀 ----------

    [Fact]
    public async Task 摘要请求回放会话提示词与区域消息作为真前缀()
    {
        var tool = AIFunctionFactory.Create(
            (string path) => "ok",
            name: "read_file",
            description: "读取一个文件。只读，不修改。");

        var request = Request() with
        {
            Instructions = "会话自己的系统提示词（逐字复用）",
            Tools = [tool]
        };
        var transport = new FakeTransport(ValidSummary());

        var result = await new LlmContextSummarizer(transport, _counter)
            .SummarizeAsync(request, CancellationToken.None);

        Assert.True(result.Ok);
        var sent = Assert.Single(transport.Requests);

        // 1. system 是会话原样，而不是摘要器自己的固定提示词。
        Assert.Equal(ChatRole.System, sent.Messages[0].Role);
        Assert.Equal("会话自己的系统提示词（逐字复用）", sent.Messages[0].Text);

        // 2. 被遮蔽区域逐条回放，role 与正文保持原样（不是压成一段带 [mN] 的文本）。
        Assert.Equal(3, sent.Messages.Count - 2); // 3 条区域消息
        Assert.Equal(ChatRole.User, sent.Messages[1].Role);
        Assert.Contains("不要修改数据库结构", sent.Messages[1].Text);
        Assert.Equal(ChatRole.Assistant, sent.Messages[2].Role);
        Assert.Equal(ChatRole.User, sent.Messages[3].Role);

        // 3. 最后一条是压缩指令。
        Assert.Equal(ChatRole.User, sent.Messages[^1].Role);
        Assert.StartsWith("现在请把上面的对话压缩成一份结构化检查点", sent.Messages[^1].Text);

        // 4. 工具声明原样带上：热前缀的第二段。
        Assert.NotNull(sent.Tools);
        Assert.Equal("read_file", Assert.Single(sent.Tools!).Name);
    }

    [Fact]
    public async Task 没有会话提示词时退回摘要器固定提示词()
    {
        var transport = new FakeTransport(ValidSummary());

        await new LlmContextSummarizer(transport, _counter)
            .SummarizeAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(transport.Requests);
        Assert.Equal(SummaryPrompt.System, sent.Messages[0].Text);
        Assert.Null(sent.Tools);
    }

    [Fact]
    public async Task 模型走工具调用没有文本时去掉工具重试一次()
    {
        // 第一次声明了工具，模型直接发起工具调用（没有正文）；去掉工具重试后拿到摘要。
        var transport = new SequenceTransport(string.Empty, ValidSummary());
        var request = Request() with
        {
            Instructions = "会话提示词",
            Tools = [AIFunctionFactory.Create((string path) => "ok", name: "read_file", description: "读取")]
        };

        var result = await new LlmContextSummarizer(transport, _counter)
            .SummarizeAsync(request, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, transport.Calls);
        Assert.NotNull(transport.Requests[0].Tools);
        Assert.Null(transport.Requests[1].Tools);
    }

    [Fact]
    public async Task 来源序号按位置换回会话里的绝对序号()
    {
        // 模型只能数「第几条消息」（因为回放的区域里没有 [mN] 标记），这里把它换回绝对序号。
        var summary = """
            ## 目标与约束
            - 不要修改数据库结构 [m1]
            ## 已确认决策
            - 改动放 docs/ [m3]
            ## 关键事实
            - 沿用旧结论 [m42]
            ## 已改动
            - 无
            ## 未完成
            - 无
            ## 用户偏好
            - 无
            """;

        var transport = new FakeTransport(summary);
        var result = await new LlmContextSummarizer(transport, _counter).SummarizeAsync(
            Request() with { ChunkStartOrder = 10 },
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("不要修改数据库结构 [m10]", result.Text);
        Assert.Contains("改动放 docs/ [m12]", result.Text);
        // 超出本批范围的序号原样保留（那通常是上一版摘要里的绝对序号）。
        Assert.Contains("沿用旧结论 [m42]", result.Text);
    }

    private sealed class SequenceTransport(params string[] responses) : ISummaryChatTransport
    {
        public int Calls { get; private set; }

        public List<SummaryChatRequest> Requests { get; } = [];

        public Task<string> CompleteAsync(SummaryChatRequest request, CancellationToken cancellationToken)
        {
            var index = Math.Min(Calls, responses.Length - 1);
            Calls++;
            Requests.Add(request);
            return Task.FromResult(responses[index]);
        }
    }

    private sealed class FakeTransport : ISummaryChatTransport
    {
        private readonly string? _text;
        private readonly TimeSpan? _delay;
        private readonly Exception? _exception;

        public FakeTransport(string? text = null, TimeSpan? delay = null, Exception? exception = null)
        {
            _text = text;
            _delay = delay;
            _exception = exception;
        }

        public List<SummaryChatRequest> Requests { get; } = [];

        public async Task<string> CompleteAsync(
            SummaryChatRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (_delay is TimeSpan delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _text ?? string.Empty;
        }
    }
}
