using System.IO;
using Microsoft.Data.Sqlite;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P1 持久化：工具轨迹、pin 标记，以及老库补摘要 / 轨迹列。</summary>
public sealed class ChatStoreContextTests : IDisposable
{
    private readonly string _root;

    public ChatStoreContextTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-chatstore-context", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task 工具轨迹与pin标记落库后能读回()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "带轨迹的会话",
            UpdatedAt = DateTime.Now
        };

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "执行脚本") { IsPinned = true });
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "执行完成")
        {
            ToolTrace =
            [
                new ToolTraceEntry("powershell", ToolTraceEntry.StatusOk, 0, "run.ps1", "成功", 120, DateTime.Now),
                new ToolTraceEntry("read_file", ToolTraceEntry.StatusError, null, null, "文件不存在", 0, DateTime.Now)
            ]
        });

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.True(loaded.Messages[0].IsPinned);

        var trace = loaded.Messages[1].ToolTrace;
        Assert.Equal(2, trace.Count);
        Assert.Equal("powershell", trace[0].ToolId);
        Assert.Equal(ToolTraceEntry.StatusOk, trace[0].Status);
        Assert.Equal("run.ps1", trace[0].ArgsDigest);
        Assert.Equal(ToolTraceEntry.StatusError, trace[1].Status);
    }

    [Fact]
    public async Task 老库自动补摘要与轨迹列()
    {
        var databasePath = Path.Combine(_root, "workspaces", "legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        CreateLegacyDatabase(databasePath);

        var store = new ChatStore(_root);
        var legacyWorkspace = new Workspace { Id = "legacy", Name = "老工作区", Path = _root };
        var loaded = Assert.Single(await store.LoadAllAsync([legacyWorkspace]));

        Assert.False(loaded.Messages[0].IsPinned);
        Assert.Empty(loaded.Messages[0].ToolTrace);

        // 补列之后新字段也要能写。
        loaded.Messages[0].IsPinned = true;
        loaded.Messages[0].ToolTrace.Add(new ToolTraceEntry(
            "read_file", ToolTraceEntry.StatusOk, null, "a.md", "ok", 10, DateTime.Now));
        await store.SaveSessionAsync(loaded);

        var reopened = Assert.Single(await store.LoadAllAsync([legacyWorkspace]));
        Assert.True(reopened.Messages[0].IsPinned);
        Assert.Single(reopened.Messages[0].ToolTrace);

        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        var sessionColumns = ReadColumns(connection, "sessions");
        Assert.Contains("Summary", sessionColumns);
        Assert.Contains("SummaryThroughOrder", sessionColumns);
        Assert.Contains("SummaryUpdatedAt", sessionColumns);
        Assert.Contains("SummaryTokens", sessionColumns);

        var messageColumns = ReadColumns(connection, "messages");
        Assert.Contains("ToolTraceJson", messageColumns);
        Assert.Contains("IsPinned", messageColumns);
    }

    private static void CreateLegacyDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE sessions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Meta TEXT NULL,
                    WorkspaceId TEXT NULL,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE messages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    "Order" INTEGER NOT NULL DEFAULT 0,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    Reasoning TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO sessions (Id, Title, Meta, IsPinned, UpdatedAt)
                VALUES ('legacy', '老会话', '刚刚', 0, '2026-01-02 03:04:05');
                INSERT INTO messages (Id, SessionId, "Order", Role, Content, Reasoning)
                VALUES ('m1', 'legacy', 0, 'User', '旧消息', '');
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";

        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
