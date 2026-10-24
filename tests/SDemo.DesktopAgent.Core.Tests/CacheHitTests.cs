using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Agents;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using DomainChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P2a 的测量项：缓存命中 token 必须从 provider usage 一路送到报告里。
/// 端点不报就保持 0，界面据此不显示命中率（宁可没有，不给假数字）。
/// </summary>
public sealed class CacheHitTests
{
    [Fact]
    public async Task 内置循环把缓存命中token透出到usage事件()
    {
        var client = new UsageOnlyChatClient(inputTokens: 1000, outputTokens: 10, cacheHitTokens: 800);
        var invocation = new AgentInvocation(
            new Uri("https://example.com/v1"),
            "not-required",
            "m",
            "你是测试助手",
            [new AIChatMessage(ChatRole.User, "你好")],
            [],
            MaxToolIterations: 2,
            NativeLoop: true);

        var events = new List<AgentStreamEvent>();

        await foreach (var evt in NativeAgentLoop.StreamAsync(
                           client,
                           invocation,
                           new ChatOptions { Instructions = "你是测试助手" }))
        {
            events.Add(evt);
        }

        var usage = Assert.Single(events.OfType<AgentStreamEvent.Usage>());
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(800, usage.CacheHitTokens);
    }

    [Fact]
    public async Task 假端点上报_prompt缓存命中时整条链路都能看到()
    {
        using var server = new FakeOpenAiServer();
        server.Respond(
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"好"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":10,"total_tokens":1010,"prompt_tokens_details":{"cached_tokens":800}}}

            data: [DONE]

            """);

        var runtime = new MafAgentRuntime();
        var invocation = new AgentInvocation(
            server.Endpoint,
            "not-required",
            "m",
            "你是测试助手",
            [new AIChatMessage(ChatRole.User, "你好")],
            [],
            MaxToolIterations: 2,
            NativeLoop: true);

        var usage = new List<AgentStreamEvent.Usage>();

        await foreach (var evt in runtime.StreamAsync(invocation))
        {
            if (evt is AgentStreamEvent.Usage item)
            {
                usage.Add(item);
            }
        }

        var reported = Assert.Single(usage);
        Assert.Equal(1000, reported.InputTokens);
        Assert.Equal(800, reported.CacheHitTokens);
    }

    [Fact]
    public void 服务端不报缓存时累计为零()
    {
        var metrics = new AgentRunMetrics();
        metrics.AddUsage(1000, 10);

        Assert.Equal(0, metrics.CacheHitTokens);

        metrics.AddUsage(500, 5, 400);
        Assert.Equal(1500, metrics.InputTokens);
        Assert.Equal(400, metrics.CacheHitTokens);
    }

    [Fact]
    public void 报告按实际输入计算命中率并在没有实际值时为零()
    {
        var report = Report() with { ActualInputTokens = 1000, CacheHitTokens = 800 };

        Assert.Equal(0.8, report.CacheHitRate);

        // 没有服务端 usage 时不能凭缓存数字编一个比例出来。
        Assert.Equal(0, (Report() with { CacheHitTokens = 800 }).CacheHitRate);
    }

    private static ContextReport Report() => new(
        ProviderId: "p",
        ModelId: "m",
        ContextWindow: 32_768,
        ReservedOutput: 4096,
        Safety: 1000,
        Total: 20_000,
        FixedCap: 8000,
        Estimated: 5000,
        RawEstimated: 5000,
        CalibrationFactor: 1.0,
        Segments: new ContextSegments(System: 1000, Skills: 0, Turns: 3000, Attachments: 0, ToolTrace: 0, Pinned: 0),
        OmittedMessages: 0,
        OmittedTokens: 0,
        KeptTurns: 2,
        TotalTurns: 2,
        PinnedCount: 0,
        PinnedTruncated: false,
        AttachmentsStubbed: false,
        Trimmed: false);

    /// <summary>只回一条 usage、不产生任何内容的假客户端。</summary>
    private sealed class UsageOnlyChatClient(int inputTokens, int outputTokens, int cacheHitTokens) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new ChatResponseUpdate
            {
                Contents =
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = inputTokens,
                        OutputTokenCount = outputTokens,
                        CachedInputTokenCount = cacheHitTokens
                    })
                ]
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
