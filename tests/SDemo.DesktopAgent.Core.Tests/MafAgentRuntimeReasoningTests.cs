using System.Net;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Agents;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 用本地假 OpenAI 兼容端点验证流式响应里的「过程内容」能不能透出来：
/// DeepSeek / LM Studio 的 <c>reasoning_content</c> 是非标准字段，工具调用也在 delta 里；
/// 这两样一旦被中间层吞掉，界面上就是「没有思维链、也不知道调了什么工具」。
/// </summary>
public sealed class MafAgentRuntimeReasoningTests
{
    [Fact]
    public async Task 流式响应里的_reasoning_content_会作为推理事件透出()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"先读一下文件。"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"reasoning_content":"再改两行。"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"好了。"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """);

        var events = await StreamAsync(server, tools: []);

        var reasoning = string.Concat(events.OfType<AgentStreamEvent.Reasoning>().Select(item => item.Value));
        var text = string.Concat(events.OfType<AgentStreamEvent.Text>().Select(item => item.Value));

        Assert.Equal("先读一下文件。再改两行。", reasoning);
        Assert.Equal("好了。", text);
    }

    [Fact]
    public async Task 工具调用与工具结果都会作为事件透出()
    {
        using var server = new FakeOpenAiServer();

        // 第一次响应：模型要求调用工具。
        server.Respond(
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":""}}]},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"a.txt\"}"}}]},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """);

        // 第二次响应：工具结果回灌后给出最终回答。
        server.Respond(
            """
            data: {"id":"2","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"读到了。"},"finish_reason":null}]}

            data: {"id":"2","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """);

        var tool = AIFunctionFactory.Create(
            (string path) => $"文件内容：{path}",
            "lookup",
            "读取工作区文件");

        var events = await StreamAsync(server, [tool]);

        var call = Assert.Single(events.OfType<AgentStreamEvent.FunctionCall>());
        Assert.Equal("lookup", call.Name);
        Assert.Equal("call_1", call.CallId);

        var result = Assert.Single(events.OfType<AgentStreamEvent.FunctionResult>());
        Assert.Contains("文件内容：a.txt", result.Result, StringComparison.Ordinal);

        Assert.Contains("读到了。", string.Concat(
            events.OfType<AgentStreamEvent.Text>().Select(item => item.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配置了思考强度时请求体里带_reasoning_effort()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """);

        await StreamAsync(server, tools: [], reasoningEffort: "high");

        var request = Assert.Single(server.RequestBodies);
        Assert.Contains("\"reasoning_effort\"", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("high", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 不思考映射成显式关闭()
    {
        // 省略字段 ≠ 不思考：Qwen3 / Ollama / LM Studio 的默认值可能就是思考，必须显式发 none。
        Assert.Equal(ReasoningEffort.None, MafAgentRuntime.ParseReasoningEffort("none"));
        Assert.Equal(ReasoningEffort.None, MafAgentRuntime.ParseReasoningEffort(" NONE "));
    }

    [Fact]
    public async Task 不思考时请求体里显式带_reasoning_effort_none()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """);

        await StreamAsync(server, tools: [], reasoningEffort: "none");

        var request = Assert.Single(server.RequestBodies);
        Assert.Matches("\"reasoning_effort\"\\s*:\\s*\"none\"", request);
    }

    [Fact]
    public async Task 没有指定思考强度时不发_reasoning_effort()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """);

        await StreamAsync(server, tools: [], reasoningEffort: null);

        var request = Assert.Single(server.RequestBodies);
        Assert.DoesNotContain("reasoning_effort", request, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<AgentStreamEvent>> StreamAsync(
        FakeOpenAiServer server,
        IReadOnlyList<AITool> tools,
        string? reasoningEffort = null,
        bool nativeLoop = false)
    {
        var runtime = new MafAgentRuntime();
        var invocation = new AgentInvocation(
            server.Endpoint,
            "not-required",
            "m",
            "你是测试助手",
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "改一下文件")],
            tools,
            3,
            ReasoningEffort: reasoningEffort,
            NativeLoop: nativeLoop);

        var events = new List<AgentStreamEvent>();

        await foreach (var evt in runtime.StreamAsync(invocation))
        {
            events.Add(evt);
        }

        return events;
    }
}
