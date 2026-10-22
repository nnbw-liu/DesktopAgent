using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P1 §3.7 spike：确认 MAF 1.25 的 <c>AsAIAgent</c> 工具循环发生在装饰器外层还是内层。
/// 结论决定中途裁剪走「装饰器（分支 A）」还是「迭代上限 + 工具结果强裁（分支 B）」。
/// 这个测试同时是结论的可执行证据，改动 MAF 版本后应当先看它。
/// </summary>
public sealed class MidTurnPipelineSpikeTests
{
    [Fact]
    public async Task AsAIAgent_工具循环在装饰器内_装饰器能看到每次请求与工具结果()
    {
        var inner = new ScriptedChatClient();

        // 装饰器放在 AsAIAgent 之前：如果 FICC 插在它上面，装饰器每次都能看到完整消息列表。
        var decorator = new ObservingChatClient(inner);
        var tool = AIFunctionFactory.Create(
            (string path) => "file content",
            name: "read_file",
            description: "读取文件");

        var agent = decorator.AsAIAgent(instructions: "测试", name: "spike", tools: [tool]);

        var text = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "读文件")]))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent { Text.Length: > 0 } chunk)
                {
                    text.Append(chunk.Text);
                }
            }
        }

        Assert.Equal("done", text.ToString());

        // 关键断言：装饰器看到两次 LLM 请求（工具调用前 / 工具结果后）。
        Assert.Equal(2, decorator.Calls.Count);
        Assert.Single(decorator.Calls[0]);

        // 第二次请求里出现了工具结果：说明 FICC 在装饰器外层执行工具。
        Assert.Contains(
            decorator.Calls[1],
            message => message.Contents.Any(content => content is FunctionResultContent));
        Assert.Contains(
            decorator.Calls[1],
            message => message.Contents.Any(content => content is FunctionCallContent { Name: "read_file" }));
        Assert.True(decorator.Calls[1].Count > decorator.Calls[0].Count);
    }

    /// <summary>记录每次收到的消息列表。</summary>
    private sealed class ObservingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<List<ChatMessage>> Calls { get; } = [];

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());

            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    /// <summary>第一次返回工具调用，第二次返回文本。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("spike 只走流式路径");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;

            if (_calls == 1)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["path"] = "a.cs" })]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]);
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
