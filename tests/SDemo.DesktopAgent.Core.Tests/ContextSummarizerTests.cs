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

    private sealed class SequenceTransport(params string[] responses) : ISummaryChatTransport
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            ModelProvider provider,
            string modelId,
            string systemPrompt,
            string userPrompt,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(Calls, responses.Length - 1);
            Calls++;
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

        public async Task<string> CompleteAsync(
            ModelProvider provider,
            string modelId,
            string systemPrompt,
            string userPrompt,
            int maxTokens,
            CancellationToken cancellationToken)
        {
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
