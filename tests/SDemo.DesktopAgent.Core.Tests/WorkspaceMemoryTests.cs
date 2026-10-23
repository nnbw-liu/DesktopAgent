using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M3：归档会话写入 FTS5，中文 2-gram + 英文缩写都能命中，取消归档后不可检索。</summary>
public sealed class WorkspaceMemoryTests : IDisposable
{
    private readonly string _root;
    private readonly ChatStore _store;
    private readonly WorkspaceMemoryIndex _memory;
    private readonly SessionArchiveService _archive;

    public WorkspaceMemoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new ChatStore(_root);
        _memory = new WorkspaceMemoryIndex(_store);
        _archive = new SessionArchiveService(_store, _memory);
    }

    [Fact]
    public async Task 二十个归档会话里能命中中文加英文缩写()
    {
        var targetId = await SeedAsync();
        await ArchiveAllAsync();

        Assert.Equal(20, _memory.CountArchived("w1"));

        var result = _memory.Search("w1", "沙箱 ACL", 5);

        Assert.NotEmpty(result.Hits);
        Assert.Equal(targetId, result.Hits[0].SessionId);
        Assert.Contains("沙箱", result.Hits[0].Snippet);
    }

    [Fact]
    public async Task 取消归档后不再被检索到()
    {
        var targetId = await SeedAsync();
        await ArchiveAllAsync();

        var session = (await _store.LoadAllAsync([new Workspace { Id = "w1", Name = "w", Path = _root }]))
            .Single(item => item.Id == targetId);

        await _archive.UnarchiveAsync(session);

        Assert.Equal(19, _memory.CountArchived("w1"));
        Assert.DoesNotContain(
            _memory.Search("w1", "沙箱 ACL", 5).Hits,
            hit => hit.SessionId == targetId);
    }

    [Fact]
    public async Task 读取归档正文时按关键词筛出相关轮次并分页()
    {
        var targetId = await SeedAsync();
        await ArchiveAllAsync();

        var page = _memory.Read("w1", targetId, "ACL", offset: 0, maxChars: 320);

        Assert.True(page.Found);
        Assert.Contains("[MEMORY_SESSION:", page.Text);
        Assert.Contains("沙箱 ACL", page.Text);
        Assert.True(page.NextOffset > 0 || page.Text.Length > 0);
    }

    [Fact]
    public async Task 老库会话可以懒重建索引()
    {
        // 先建索引再清空 FTS 表，模拟手工拷贝 / 上次崩溃：搜索前 EnsureIndexed 应整库重建。
        await SeedAsync();
        await ArchiveAllAsync();

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={_store.GetWorkspaceDatabasePath("w1")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM archived_memory_fts";
            command.ExecuteNonQuery();
        }

        var result = _memory.Search("w1", "沙箱 ACL", 3);

        Assert.NotEmpty(result.Hits);
    }

    private async Task<string> SeedAsync()
    {
        string? targetId = null;

        for (var index = 0; index < 20; index++)
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new ChatSession
            {
                Id = id,
                Title = index == 7 ? "沙箱隔离复盘" : $"归档会话 {index}",
                WorkspaceId = "w1",
                UpdatedAt = DateTime.Now.AddMinutes(-index)
            };

            if (index == 7)
            {
                targetId = id;
                session.Messages.Add(new ChatMessage(ChatMessageRole.User, "上次沙箱 ACL 的结论是什么？"));
                session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant,
                    "结论：沙箱 ACL 需要给工作区副本授予读写权限，同时保持用户目录不可读。"));
            }
            else
            {
                session.Messages.Add(new ChatMessage(ChatMessageRole.User, $"第 {index} 个会话的问题"));
                session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, $"第 {index} 个会话的普通回答。"));
            }

            await _store.SaveSessionAsync(session);
        }

        return targetId!;
    }

    private async Task ArchiveAllAsync()
    {
        var sessions = await _store.LoadAllAsync([new Workspace { Id = "w1", Name = "w", Path = _root }]);

        foreach (var session in sessions)
        {
            var outcome = await _archive.ArchiveAsync(session);
            Assert.True(outcome.Changed, outcome.Message);
        }
    }

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
