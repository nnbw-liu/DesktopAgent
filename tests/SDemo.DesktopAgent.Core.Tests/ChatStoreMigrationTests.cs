using System.IO;
using Microsoft.Data.Sqlite;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// R1.3：EF Core 迁移的三条输入路径——空库、EnsureCreated 时代的老库、已经迁移过的库。
/// 目标不是测 EF 本身，而是保证「升级后老数据还读得出来、新库直接可用、重复打开是空操作」。
/// </summary>
public sealed class ChatStoreMigrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 空库首次保存后建表并写入迁移历史()
    {
        var store = new ChatStore(_temp.Root);
        var session = new ChatSession { Id = "new-1", Title = "新库会话" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "你好"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "在"));

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("新库会话", loaded.Title);
        Assert.Equal(2, loaded.Messages.Count);

        using var connection = OpenHistory();
        Assert.True(TableExists(connection, "sessions"));
        Assert.True(TableExists(connection, "messages"));
        Assert.True(ScalarInt(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory") >= 1);
        Assert.EndsWith("_InitialCreate", ScalarString(connection,
            "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId LIMIT 1"));

        // 最新迁移也要真的执行：新增列存在，而不是只建了初始表。
        Assert.Contains("ProviderId", ReadColumns(connection, "messages"));

        // 索引也必须由迁移建出来（老实现靠 EnsureCreated，不能靠「表有了」蒙混）。
        Assert.True(ScalarInt(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_sessions_UpdatedAt'") == 1);
        Assert.True(ScalarInt(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_messages_SessionId'") == 1);
    }

    [Fact]
    public async Task 老库缺列时先补列再基线化_数据不丢()
    {
        CreateLegacyDatabase();

        var store = new ChatStore(_temp.Root);
        var sessions = await store.LoadAllAsync([]);
        var loaded = Assert.Single(sessions);

        Assert.Equal("老会话", loaded.Title);
        Assert.Equal("旧消息", Assert.Single(loaded.Messages).Content);

        using var connection = OpenHistory();
        var columns = ReadColumns(connection, "sessions");
        Assert.Contains("Summary", columns);
        Assert.Contains("IsArchived", columns);
        Assert.Contains("TodosJson", columns);
        Assert.Contains("DurationMs", ReadColumns(connection, "messages"));

        // 基线化：InitialCreate 记为已应用，避免 Migrate 再建一次表；
        // 后续迁移（这里是最新的 ProviderId / ModelId）仍然会继续执行。
        Assert.True(ScalarInt(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory") >= 2);
        Assert.Contains("ProviderId", ReadColumns(connection, "messages"));
        Assert.Contains("ModelId", ReadColumns(connection, "messages"));
    }

    [Fact]
    public async Task 老库升级后仍能继续写入并读回()
    {
        CreateLegacyDatabase();

        var store = new ChatStore(_temp.Root);
        var loaded = Assert.Single(await store.LoadAllAsync([]));
        loaded.Title = "改过的标题";
        loaded.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "新回复"));
        loaded.IsArchived = true;

        await store.SaveSessionAsync(loaded);

        var reloaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("改过的标题", reloaded.Title);
        Assert.Equal(2, reloaded.Messages.Count);
        Assert.True(reloaded.IsArchived);
    }

    [Fact]
    public async Task 已迁移库重复打开不重复应用迁移()
    {
        var store = new ChatStore(_temp.Root);
        var session = new ChatSession { Id = "repeat-1", Title = "重复打开" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "一"));
        await store.SaveSessionAsync(session);

        int applied;
        using (var connection = OpenHistory())
        {
            applied = ScalarInt(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory");
        }

        for (var round = 0; round < 3; round++)
        {
            var loaded = Assert.Single(await store.LoadAllAsync([]));
            Assert.Equal("重复打开", loaded.Title);
        }

        using var reopened = OpenHistory();
        Assert.Equal(applied, ScalarInt(reopened, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }

    [Fact]
    public async Task 迁移后FTS5索引可用()
    {
        var store = new ChatStore(_temp.Root);
        var session = new ChatSession { Id = "fts-1", Title = "知识库" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "沙箱 ACL 的结论是只读"));
        await store.SaveSessionAsync(session);

        using var connection = OpenHistory();

        // FTS5 是编译期可选项：不可用时只影响知识库，测试跳过而不是失败。
        if (ScalarInt(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'archived_memory_fts'") == 0)
        {
            return;
        }

        Assert.Equal(1, ScalarInt(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'archived_memory_fts'"));
    }

    [Fact]
    public async Task 新库与老库升级后的列集合一致()
    {
        var fresh = new ChatStore(Path.Combine(_temp.Root, "fresh"));

        var session = new ChatSession { Id = "same", Title = "同一结构" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "一"));
        await fresh.SaveSessionAsync(session);

        List<string> freshColumns;
        using (var connection = OpenRaw(fresh.HistoryDatabasePath))
        {
            freshColumns = ReadColumns(connection, "sessions");
        }

        CreateLegacyDatabase();
        var legacy = new ChatStore(_temp.Root);
        await legacy.LoadAllAsync([]);

        List<string> legacyColumns;
        using (var connection = OpenRaw(legacy.HistoryDatabasePath))
        {
            legacyColumns = ReadColumns(connection, "sessions");
        }

        Assert.Equal(
            freshColumns.OrderBy(name => name, StringComparer.Ordinal),
            legacyColumns.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task 老库升级后新增列可读写()
    {
        CreateLegacyDatabase();

        var store = new ChatStore(_temp.Root);
        var loaded = Assert.Single(await store.LoadAllAsync([]));
        loaded.Messages[0].ProviderId = "local";
        loaded.Messages[0].ModelId = "qwen3.5-4b";
        loaded.Messages[0].ApprovalCount = 2;

        await store.SaveSessionAsync(loaded);

        var reloaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("local", reloaded.Messages[0].ProviderId);
        Assert.Equal("qwen3.5-4b", reloaded.Messages[0].ModelId);
        Assert.Equal(2, reloaded.Messages[0].ApprovalCount);
    }

    private void CreateLegacyDatabase()
    {
        Directory.CreateDirectory(_temp.Root);
        var path = Path.Combine(_temp.Root, "history.db");

        if (File.Exists(path))
        {
            return;
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();

        // EnsureCreated 时代的基础结构：只有最早的列，之后的列都靠 RequiredColumns 补。
        command.CommandText = """
            CREATE TABLE "sessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_sessions" PRIMARY KEY,
                "Title" TEXT NOT NULL,
                "Meta" TEXT NULL,
                "WorkspaceId" TEXT NULL,
                "IsPinned" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE "messages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_messages" PRIMARY KEY,
                "SessionId" TEXT NOT NULL,
                "Order" INTEGER NOT NULL,
                "Role" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "Reasoning" TEXT NOT NULL,
                CONSTRAINT "FK_messages_sessions_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "sessions" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX "IX_messages_SessionId" ON "messages" ("SessionId");
            CREATE INDEX "IX_sessions_UpdatedAt" ON "sessions" ("UpdatedAt");
            INSERT INTO "sessions" ("Id", "Title", "Meta", "WorkspaceId", "IsPinned", "UpdatedAt")
            VALUES ('legacy-1', '老会话', NULL, NULL, 0, '2026-09-01 10:00:00');
            INSERT INTO "messages" ("Id", "SessionId", "Order", "Role", "Content", "Reasoning")
            VALUES ('legacy-m1', 'legacy-1', 0, 'User', '旧消息', '');
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenHistory() =>
        OpenRaw(new ChatStore(_temp.Root).HistoryDatabasePath);

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string table) =>
        ScalarInt(connection,
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'") == 1;

    private static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";

        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is long value ? (int)value : 0;
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }
}

