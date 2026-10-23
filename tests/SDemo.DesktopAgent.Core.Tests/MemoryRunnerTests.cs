using System.Runtime.CompilerServices;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// M3 接线：Runner 只在「开启 ∩ 有工作区 ∩ 有归档会话」时注入知识库索引行；
/// 关闭后上下文成本必须回到 0。
/// </summary>
public sealed class MemoryRunnerTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    [Fact]
    public void 有归档会话时只注入一句索引()
    {
        var runner = CreateRunner(new StubMemory(7));
        var plan = runner.Preview(Request(WorkspaceSession()));

        Assert.Contains("## 工作区知识库", plan.Instructions);
        Assert.Contains("7 条归档会话", plan.Instructions);
        Assert.True(plan.Report.Segments.Knowledge > 0);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
    }

    [Fact]
    public void 关闭知识库时上下文成本为零()
    {
        _settings.Current.Agent.WorkspaceMemoryEnabled = false;
        var runner = CreateRunner(new StubMemory(7));
        var plan = runner.Preview(Request(WorkspaceSession()));

        Assert.DoesNotContain("工作区知识库", plan.Instructions);
        Assert.Equal(0, plan.Report.Segments.Knowledge);
    }

    [Fact]
    public void 没有工作区时不注入()
    {
        var runner = CreateRunner(new StubMemory(7));
        var session = WorkspaceSession();
        session.WorkspaceId = null;

        var plan = runner.Preview(Request(session));

        Assert.DoesNotContain("工作区知识库", plan.Instructions);
        Assert.Equal(0, plan.Report.Segments.Knowledge);
    }

    private AgentSessionRunner CreateRunner(IWorkspaceMemory memory)
    {
        var builder = new ContextBuilder(_counter, _settings);
        return new AgentSessionRunner(new EmptyChat(), builder, _settings, memory: memory);
    }

    private static ChatSession WorkspaceSession() => new()
    {
        Id = "s1",
        Title = "当前会话",
        WorkspaceId = "w1",
        UpdatedAt = DateTime.Now
    };

    private static AgentRunRequest Request(ChatSession session) => new(
        [new ChatMessage(ChatMessageRole.User, "上次沙箱 ACL 的结论")],
        new ModelProvider
        {
            Id = "p",
            DisplayName = "测试",
            BaseUrl = "https://example.com/v1",
            Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
        },
        "m",
        new HashSet<string>(StringComparer.Ordinal),
        SessionId: "s1",
        Session: session);

    private sealed class EmptyChat : IAgentChatService
    {
        public async IAsyncEnumerable<AgentDelta> RunAsync(
            AgentRunPlan plan,
            ContextRunTelemetry? telemetry = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubMemory(int count) : IWorkspaceMemory
    {
        public bool IsAvailable(string? workspaceId) => count > 0;

        public int CountArchived(string? workspaceId) => count;

        public IReadOnlyList<MemorySessionCard> ListArchived(string? workspaceId) => [];

        public MemorySearchResult Search(string? workspaceId, string query, int topK) =>
            MemorySearchResult.Empty(query, count);

        public MemoryReadResult Read(
            string? workspaceId,
            string sessionId,
            string? query,
            int offset,
            int maxChars) =>
            new(false, sessionId, string.Empty, DateTime.MinValue, string.Empty, 0);

        public void Index(string? workspaceId, string sessionId)
        {
        }

        public void Remove(string? workspaceId, string sessionId)
        {
        }

        public void Rebuild(string? workspaceId)
        {
        }
    }
}
