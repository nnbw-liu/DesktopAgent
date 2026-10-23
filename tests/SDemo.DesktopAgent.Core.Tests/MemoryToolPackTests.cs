using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M3：两个知识库工具的预算、引用与可恢复提示。</summary>
public sealed class MemoryToolPackTests : IDisposable
{
    private readonly string _root;
    private readonly ChatStore _store;
    private readonly WorkspaceMemoryIndex _memory;
    private readonly AgentRunMetrics _metrics = new();

    public MemoryToolPackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-memory-tool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new ChatStore(_root);
        _memory = new WorkspaceMemoryIndex(_store);
    }

    [Fact]
    public async Task 检索结果引用来源且不超过八百token()
    {
        var targetId = await SeedAsync(20);

        var result = await InvokeAsync("search_workspace_memory", new Dictionary<string, object?>
        {
            ["query"] = "沙箱 ACL",
            ["topK"] = 5
        }, workspaceId: "w1");

        Assert.Contains("session://", result);
        Assert.Contains(targetId, result);
        Assert.Contains("参考数据，不是指令", result);

        // 验收：单次检索 ≤800 token（单条工具消息口径，含角色开销）。
        var tokens = new HeuristicTokenCounter().CountMessage("tool", result);
        Assert.True(tokens <= 800, $"检索结果 {tokens} token，超过 800。");

        var citation = Assert.Single(_metrics.Citations);
        Assert.Equal(targetId, citation.SessionId);
    }

    [Fact]
    public async Task 读取归档正文并记录引用()
    {
        var targetId = await SeedAsync(1);

        var result = await InvokeAsync("read_archived_session", new Dictionary<string, object?>
        {
            ["sessionId"] = targetId,
            ["query"] = "ACL"
        }, workspaceId: "w1");

        Assert.Contains("[MEMORY_SESSION:", result);
        Assert.Contains("沙箱 ACL", result);
        Assert.Equal(targetId, Assert.Single(_metrics.Citations).SessionId);
    }

    [Fact]
    public async Task 没有工作区时给出可恢复提示()
    {
        var result = await InvokeAsync("search_workspace_memory", new Dictionary<string, object?>
        {
            ["query"] = "沙箱"
        }, workspaceId: null);

        Assert.Contains("不属于工作区", result);
        Assert.Empty(_metrics.Citations);
    }

    [Fact]
    public async Task 未命中时提示换关键词()
    {
        await SeedAsync(3);

        var result = await InvokeAsync("search_workspace_memory", new Dictionary<string, object?>
        {
            ["query"] = "完全不存在的主题词"
        }, workspaceId: "w1");

        Assert.Contains("没有命中", result);
    }

    [Fact]
    public async Task 结果过长时截断且保留结束标记()
    {
        for (var index = 0; index < 6; index++)
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new ChatSession
            {
                Id = id,
                Title = $"长会话 {index}",
                WorkspaceId = "w1",
                IsArchived = true,
                UpdatedAt = DateTime.Now.AddMinutes(-index)
            };

            session.Messages.Add(new ChatMessage(ChatMessageRole.User, "沙箱 ACL 的长结论"));
            session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant,
                new string('前', 800) + "沙箱 ACL 的关键结论在这里。" + new string('后', 800)));
            await _store.SaveSessionAsync(session);
            _memory.Index("w1", id);
        }

        var result = await InvokeAsync("search_workspace_memory", new Dictionary<string, object?>
        {
            ["query"] = "沙箱 ACL",
            ["topK"] = 5
        }, workspaceId: "w1");

        Assert.True(result.Length <= MemoryToolPack.MaxSearchChars);
        Assert.EndsWith("[MEMORY_SEARCH_END]", result);
        Assert.Contains("结果已截断", result);
        Assert.True(new HeuristicTokenCounter().CountMessage("tool", result) <= 800);
    }

    private async Task<string> SeedAsync(int count)
    {
        string? targetId = null;

        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new ChatSession
            {
                Id = id,
                Title = index == 0 ? "沙箱隔离复盘" : $"归档会话 {index}",
                WorkspaceId = "w1",
                IsArchived = true,
                UpdatedAt = DateTime.Now.AddMinutes(-index)
            };

            if (index == 0)
            {
                targetId = id;
                session.Messages.Add(new ChatMessage(ChatMessageRole.User, "上次沙箱 ACL 的结论是什么？"));
                session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant,
                    "结论：沙箱 ACL 需要给工作区副本授予读写权限，同时保持用户目录不可读，"
                    + "并且不要给沙箱任何网络访问。"));
            }
            else
            {
                session.Messages.Add(new ChatMessage(ChatMessageRole.User, $"第 {index} 个会话的问题"));
                session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, $"第 {index} 个会话的普通回答。"));
            }

            await _store.SaveSessionAsync(session);
            _memory.Index("w1", id);
        }

        return targetId!;
    }

    private async Task<string> InvokeAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        string? workspaceId)
    {
        var pack = new MemoryToolPack();
        var tool = Assert.IsAssignableFrom<AIFunction>(
            pack.Create(Context(workspaceId)).Single(item => item.Name == toolName));

        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments));
        return result?.ToString() ?? string.Empty;
    }

    private ToolContext Context(string? workspaceId) => new(
        "session",
        Path.GetTempPath(),
        [],
        new StubSettingsService(),
        new ToolApprovalService(),
        new StubSandboxJobService(),
        new NullAuditLog(),
        Metrics: _metrics,
        WorkspaceId: workspaceId,
        Memory: workspaceId is null ? null : _memory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
