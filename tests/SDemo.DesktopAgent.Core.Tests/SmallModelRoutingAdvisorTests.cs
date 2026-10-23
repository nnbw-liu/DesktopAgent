using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// M4 小模型路由：JSON 解析 / 白名单过滤 / 缓存 / 失败不抛异常。
/// 不联网——所有响应都由 FakeTransport 注入。
/// </summary>
public sealed class SmallModelRoutingAdvisorTests
{
    [Fact]
    public void 解析合法JSON并按白名单过滤未知工具()
    {
        var advice = SmallModelRoutingAdvisor.Parse(
            """{"tools":["pdf_read","not_a_tool","read_file"],"intent":"pdf","reason":"读 PDF","confidence":0.8}""",
            Capabilities());

        Assert.NotNull(advice);
        Assert.Equal(["pdf_read", "read_file"], advice!.Tools);
        Assert.Equal("pdf", advice.Intent);
        Assert.Equal(0.8, advice.Confidence);
    }

    [Fact]
    public void 支持Markdown代码块与大小写不敏感()
    {
        var advice = SmallModelRoutingAdvisor.Parse(
            "```json\n{\"tools\":[\"PDF_READ\",\"Read_File\"]}\n```",
            Capabilities());

        Assert.NotNull(advice);
        Assert.Equal(["pdf_read", "read_file"], advice!.Tools);
    }

    [Fact]
    public void 非法JSON或空工具返回空建议()
    {
        Assert.Null(SmallModelRoutingAdvisor.Parse("not json", Capabilities()));
        Assert.Null(SmallModelRoutingAdvisor.Parse("""{"tools":[]}""", Capabilities()));
        Assert.Null(SmallModelRoutingAdvisor.Parse("""{"tools":["unknown"]}""", Capabilities()));
    }

    [Fact]
    public async Task 传输失败时返回空而不抛出()
    {
        var transport = new FakeTransport { Failure = new HttpRequestException("boom") };
        var advisor = new SmallModelRoutingAdvisor(transport);

        var advice = await advisor.AdviseAsync(Request());

        Assert.Null(advice);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task 同一输入命中缓存只调用一次()
    {
        var transport = new FakeTransport
        {
            Reply = """{"tools":["read_file"],"intent":"read","confidence":0.9}"""
        };
        var advisor = new SmallModelRoutingAdvisor(transport);

        var first = await advisor.AdviseAsync(Request());
        var second = await advisor.AdviseAsync(Request());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(["read_file"], second!.Tools);
    }

    [Fact]
    public void 提示包含上限工具清单与JSONSchema()
    {
        var prompt = SmallModelRoutingAdvisor.BuildUserPrompt(Request());

        Assert.Contains("最多 6 个工具", prompt);
        Assert.Contains("pdf_read", prompt);
        Assert.Contains("JSON Schema", prompt);
        Assert.Contains("\"tools\"", prompt);
    }

    private static RoutingAdviceRequest Request() => new(
        Provider(),
        "router",
        "帮我看看这个 PDF 讲了什么",
        [],
        AgentMode.Full,
        6,
        Capabilities(),
        []);

    private static IReadOnlyList<RoutingCapability> Capabilities() =>
    [
        new("read_file", "files", "文件（只读）", "按行读取工作区文本文件。"),
        new("search_files", "files", "文件（只读）", "按正则搜索工作区文本。"),
        new("apply_patch", "edits", "代码编辑", "应用 unified diff 修改文件。"),
        new("pdf_read", "pdf", "PDF", "在沙箱中读取 PDF 文本。"),
        new("web_search", "search", "联网搜索", "调用搜索后端。"),
        new("search_workspace_memory", "memory", "知识库", "检索归档会话。")
    ];

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "router" }]
    };

    private sealed class FakeTransport : IRoutingChatTransport
    {
        public int Calls { get; private set; }

        public string Reply { get; init; } = "{}";

        public Exception? Failure { get; init; }

        public Task<string> CompleteAsync(
            ModelProvider provider,
            string modelId,
            string systemPrompt,
            string userPrompt,
            string jsonSchema,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null
                ? Task.FromResult(Reply)
                : Task.FromException<string>(Failure);
        }
    }
}
