using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Agents;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P1a：内置工具循环（<c>NativeAgentLoop</c>）对假端点的行为契约。
/// 用本地 SSE 服务器看「发出去的请求长什么样」，这比断言事件列表更能说明工具历史有没有保真。
/// </summary>
public sealed class NativeAgentLoopTests
{
    [Fact]
    public async Task 工具调用与结果会作为真实消息回灌到下一次请求()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "lookup", """{"path":"a.txt"}"""));
        server.Respond(Text("读到了。"));

        var tool = AIFunctionFactory.Create((string path) => $"文件内容：{path}", "lookup", "读取工作区文件");

        var events = await StreamAsync(server, [tool], maxToolIterations: 4);

        // step 边界：两次请求，各有一对 start/end。
        Assert.Equal([0, 1], events.OfType<AgentStreamEvent.StepStarted>().Select(step => step.Step));
        Assert.Equal([0, 1], events.OfType<AgentStreamEvent.StepCompleted>().Select(step => step.Step));

        var call = Assert.Single(events.OfType<AgentStreamEvent.FunctionCall>());
        Assert.Equal(0, call.Step);

        var result = Assert.Single(events.OfType<AgentStreamEvent.FunctionResult>());
        Assert.Equal(call.CallId, result.CallId);
        Assert.Contains("文件内容：a.txt", result.Result, StringComparison.Ordinal);

        // 第二次请求里能看到 assistant 的 tool_calls 与配对的 tool 消息——不是压成一段文本。
        var second = server.RequestBodies[1];
        Assert.Contains("\"tool_calls\"", second, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"tool\"", second, StringComparison.Ordinal);
        Assert.Contains("\"tool_call_id\":\"call_1\"", second, StringComparison.Ordinal);
        Assert.Contains("文件内容：a.txt", second, StringComparison.Ordinal);

        Assert.Contains("读到了。", string.Concat(
            events.OfType<AgentStreamEvent.Text>().Select(delta => delta.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 同一次响应里的多个工具按顺序执行并各自配对()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(TwoToolCalls());
        server.Respond(Text("都读完了。"));

        var executed = new List<string>();
        var first = AIFunctionFactory.Create(
            (string path) => { executed.Add($"lookup:{path}"); return $"内容：{path}"; },
            "lookup",
            "读取工作区文件");
        var second = AIFunctionFactory.Create(
            (string path) => { executed.Add($"peek:{path}"); return $"标题：{path}"; },
            "peek",
            "看文件头");

        var events = await StreamAsync(server, [first, second], maxToolIterations: 4);

        Assert.Equal(["lookup:a.txt", "peek:b.txt"], executed);

        var calls = events.OfType<AgentStreamEvent.FunctionCall>().ToArray();
        var results = events.OfType<AgentStreamEvent.FunctionResult>().ToArray();

        Assert.Equal(["call_1", "call_2"], calls.Select(call => call.CallId));
        Assert.Equal(calls.Select(call => call.CallId), results.Select(result => result.CallId));

        var body = server.RequestBodies[1];
        Assert.Contains("\"tool_call_id\":\"call_1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"tool_call_id\":\"call_2\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 最后一次请求不再声明工具_模型必须收尾()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "lookup", """{"path":"a.txt"}"""));

        var tool = AIFunctionFactory.Create((string path) => $"文件内容：{path}", "lookup", "读取工作区文件");

        var events = await StreamAsync(server, [tool], maxToolIterations: 2);

        var bodies = server.RequestBodies;
        Assert.Equal(2, bodies.Count);

        // 第一次请求带工具声明（有 parameters schema）；最后一次没有，工具调用也不再执行。
        Assert.Contains("\"parameters\"", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"parameters\"", bodies[1], StringComparison.Ordinal);

        Assert.Single(events.OfType<AgentStreamEvent.FunctionResult>());
        Assert.Equal([0, 1], events.OfType<AgentStreamEvent.StepStarted>().Select(step => step.Step));
    }

    [Fact]
    public async Task 未知工具不执行_只回一条可改的错误结果()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_9", "missing_tool", "{}"));
        server.Respond(Text("那我换个方式。"));

        var tool = AIFunctionFactory.Create((string path) => path, "lookup", "读取工作区文件");

        var events = await StreamAsync(server, [tool], maxToolIterations: 4);

        var result = Assert.Single(events.OfType<AgentStreamEvent.FunctionResult>());
        Assert.Contains("本轮没有提供工具", result.Result, StringComparison.Ordinal);

        // 错误结果回灌给模型，下一轮才有机会换工具/换做法。
        Assert.Contains("本轮没有提供工具", server.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("那我换个方式。", string.Concat(
            events.OfType<AgentStreamEvent.Text>().Select(delta => delta.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有工具调用时只有一步()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(Text("直接回答。"));

        var events = await StreamAsync(server, [], maxToolIterations: 3);

        Assert.Single(events.OfType<AgentStreamEvent.StepStarted>());
        Assert.Single(events.OfType<AgentStreamEvent.StepCompleted>());
        Assert.Empty(events.OfType<AgentStreamEvent.FunctionCall>());
        Assert.Single(server.RequestBodies);
    }

    [Fact]
    public async Task 上下文裁剪装饰器在每一次请求上都会运行()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "lookup", """{"path":"a.txt"}"""));
        server.Respond(Text("读到了。"));

        var tool = AIFunctionFactory.Create((string path) => $"文件内容：{path}", "lookup", "读取工作区文件");
        var requests = 0;

        await StreamAsync(
            server,
            [tool],
            maxToolIterations: 4,
            decorate: inner => new CountingChatClient(inner, () => requests++));

        // FICC 时代装饰器靠「每轮回调」生效；自建循环必须显式每次都经过它（裁剪 / 计量不能只跑一次）。
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task 参数不是合法_JSON_时不炸整轮_而是回一条可修复的错误()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "lookup", "{path:"));
        server.Respond(Text("我重新读一次。"));

        var tool = AIFunctionFactory.Create((string path) => $"文件内容：{path}", "lookup", "读取工作区文件");

        var events = await StreamAsync(server, [tool], maxToolIterations: 4);

        // 坏参数不许把整轮打断：工具不执行，回一条失败文本，模型还有下一轮。
        var result = Assert.Single(events.OfType<AgentStreamEvent.FunctionResult>());
        Assert.Contains("工具执行失败", result.Result, StringComparison.Ordinal);

        Assert.Equal(2, server.RequestBodies.Count);
        Assert.Contains("我重新读一次。", string.Concat(
            events.OfType<AgentStreamEvent.Text>().Select(delta => delta.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有声明工具时_模型硬要调用只回一次错误就收尾()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "lookup", """{"path":"a.txt"}"""));
        server.Respond(Text("我不该调用工具。"));

        var events = await StreamAsync(server, [], maxToolIterations: 4);

        // 没有工具可执行：回一条「未提供」，不再多问一轮（否则会白烧轮数）。
        Assert.Single(server.RequestBodies);

        var result = Assert.Single(events.OfType<AgentStreamEvent.FunctionResult>());
        Assert.Contains("本轮没有提供工具", result.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 本轮内补工具后下一步请求就带上新工具()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "search_capabilities", """{"query":"pdf"}"""));
        server.Respond(ToolCall("call_2", "pdf_read", """{"path":"a.pdf"}"""));
        server.Respond(Text("读完了。"));

        var search = AIFunctionFactory.Create(
            (string query) => "匹配：pdf_read（本轮未暴露）", "search_capabilities", "查能力");
        var pdf = AIFunctionFactory.Create((string path) => $"PDF 文本 {path}", "pdf_read", "读 PDF");

        var expandedCalls = 0;

        var events = await StreamAsync(
            server,
            [search],
            maxToolIterations: 4,
            expandTools: call =>
            {
                expandedCalls++;

                return call.ToolName == "search_capabilities" ? [pdf] : [];
            });

        // 宿主把「补了哪个工具」告诉界面（过程组里的一行），而不是静默换牌。
        var expanded = Assert.Single(events.OfType<AgentStreamEvent.ToolsExpanded>());
        Assert.Equal(["pdf_read"], expanded.Tools);

        // 同一轮、下一步请求就声明了新工具，而且模型真的用上了。
        Assert.Equal(3, server.RequestBodies.Count);
        Assert.Contains("pdf_read", server.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("PDF 文本 a.pdf", server.RequestBodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 本轮补工具只补一次()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(ToolCall("call_1", "search_capabilities", """{"query":"pdf"}"""));
        server.Respond(ToolCall("call_2", "search_capabilities", """{"query":"pdf"}"""));
        server.Respond(Text("还是用它吧。"));

        var search = AIFunctionFactory.Create(
            (string query) => "匹配：pdf_read（本轮未暴露）", "search_capabilities", "查能力");
        var pdf = AIFunctionFactory.Create((string path) => $"PDF 文本 {path}", "pdf_read", "读 PDF");

        var expandedCalls = 0;

        var events = await StreamAsync(
            server,
            [search],
            maxToolIterations: 5,
            expandTools: call =>
            {
                expandedCalls++;
                return call.ToolName == "search_capabilities" ? [pdf] : [];
            });

        // 有界：一次轮次里只补一次，避免「边跑边全量暴露」。
        Assert.Equal(1, expandedCalls);
        Assert.Single(events.OfType<AgentStreamEvent.ToolsExpanded>());
    }

    private static async Task<List<AgentStreamEvent>> StreamAsync(
        FakeOpenAiServer server,
        IReadOnlyList<AITool> tools,
        int maxToolIterations,
        Func<IChatClient, IChatClient>? decorate = null,
        Func<AgentToolCallRequest, IReadOnlyList<AITool>>? expandTools = null)
    {
        var runtime = new MafAgentRuntime();
        var invocation = new AgentInvocation(
            server.Endpoint,
            "not-required",
            "m",
            "你是测试助手",
            [new ChatMessage(ChatRole.User, "看下 a.txt")],
            tools,
            maxToolIterations,
            Decorate: decorate,
            NativeLoop: true,
            ExpandTools: expandTools);

        var events = new List<AgentStreamEvent>();

        await foreach (var evt in runtime.StreamAsync(invocation))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>数一数经过了几次请求的装饰器。</summary>
    private sealed class CountingChatClient(IChatClient inner, Action onRequest) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            onRequest();

            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    /// <summary>一段纯文本响应。</summary>
    private static string Text(string text) =>
        """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"__TEXT__"},"finish_reason":null}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]

        """.Replace("__TEXT__", Escape(text), StringComparison.Ordinal);

    /// <summary>一段「要求调用一个工具」的响应（参数分两片到达）。</summary>
    private static string ToolCall(string callId, string name, string arguments) =>
        """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"__CALL_ID__","type":"function","function":{"name":"__NAME__","arguments":""}}]},"finish_reason":null}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"__ARGS__"}}]},"finish_reason":null}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """.Replace("__CALL_ID__", callId, StringComparison.Ordinal)
            .Replace("__NAME__", name, StringComparison.Ordinal)
            .Replace("__ARGS__", Escape(arguments), StringComparison.Ordinal);

    /// <summary>一次响应里要求调用两个工具。</summary>
    private static string TwoToolCalls() =>
        """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"path\":\"a.txt\"}"}},{"index":1,"id":"call_2","type":"function","function":{"name":"peek","arguments":"{\"path\":\"b.txt\"}"}}]},"finish_reason":null}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """;

    private static string Escape(string json) => json.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
