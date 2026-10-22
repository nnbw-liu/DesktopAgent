using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>工具循环内的中途裁剪（§3.7 分支 A）：强裁工具结果、整块淘汰、保留 system 与当前块。</summary>
public sealed class ContextBudgetChatClientTests
{
    private readonly HeuristicTokenCounter _counter = new();

    private ContextBudgetChatOptions Options(bool enabled, int toolResultChars = 200, int total = 1200) => new(
        Enabled: enabled,
        Budget: new ContextBudget(8192, 2048, 409, total, 400, 300),
        Counter: _counter,
        CalibrationFactor: 1.0,
        KeepRecentTurns: 2,
        ToolResultChars: toolResultChars);

    private static ChatMessage ToolResult(string callId, string text) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, text)]);

    private static ChatMessage Call(string callId, string name, string argument) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>
        {
            ["path"] = argument
        })]);

    [Fact]
    public async Task 过长的工具结果在下次请求前被截断()
    {
        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry();
        var client = new ContextBudgetChatClient(inner, Options(enabled: true), telemetry);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "系统提示"),
            new(ChatRole.User, "读文件"),
            Call("c1", "read_file", "big.txt"),
            ToolResult("c1", new string('x', 5000))
        };

        await client.GetResponseAsync(messages);

        var sent = Assert.Single(inner.Calls);
        var result = sent.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        var text = result.Result!.ToString()!;

        Assert.True(text.Length < 2000);
        Assert.Contains("工具结果过长，已省略", text);
        Assert.Equal(1, telemetry.MidTurnTrimmedRequests);
    }

    [Fact]
    public async Task 超预算时从最旧整块淘汰_保留system与当前块()
    {
        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry();
        var client = new ContextBudgetChatClient(inner, Options(enabled: true, toolResultChars: 100_000, total: 900), telemetry);

        var messages = new List<ChatMessage> { new(ChatRole.System, "系统提示") };
        var current = "问题 3" + new string('问', 400);

        for (var index = 0; index < 4; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"问题 {index}" + new string('问', 400)));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"回答 {index}" + new string('答', 400)));
        }

        await client.GetResponseAsync(messages);

        var sent = Assert.Single(inner.Calls);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal(ChatRole.User, sent[1].Role);
        // 最后一块（当前用户输入及其后的回答）一定在。
        Assert.Equal(current, sent[1].Text);
        Assert.True(sent.Count < messages.Count);
        Assert.Equal(1, telemetry.MidTurnTrimmedRequests);
    }

    [Fact]
    public async Task 关闭中途裁剪时原样发送()
    {
        var inner = new RecordingChatClient();
        var client = new ContextBudgetChatClient(inner, Options(enabled: false), new ContextRunTelemetry());

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('长', 5000)),
            ToolResult("c1", new string('x', 5000))
        };

        await client.GetResponseAsync(messages);

        var sent = Assert.Single(inner.Calls);
        Assert.Equal(messages.Count, sent.Count);
    }

    [Fact]
    public async Task 每次请求都累计估算与请求次数()
    {
        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry { Report = Report() };
        var client = new ContextBudgetChatClient(inner, Options(enabled: true), telemetry);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(2, telemetry.RequestCount);
        Assert.True(telemetry.EstimatedInputTokens > 0);
    }

    private static ContextReport Report() => new(
        "p", "m", 8192, 2048, 409, 4874, 1949, 10, 10, 1.0,
        new ContextSegments(10, 0, 0, 0, 0, 0),
        0, 0, 0, 0, 0, false, false, false);

    private sealed class RecordingChatClient : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
