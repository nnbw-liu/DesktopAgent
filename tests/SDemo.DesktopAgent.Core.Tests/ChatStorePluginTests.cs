using System.IO;
using Microsoft.Data.Sqlite;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>会话持久化：插件字段、消息归属，以及老库补列。</summary>
public sealed class ChatStorePluginTests : IDisposable
{
    private readonly string _root;

    public ChatStorePluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-chatstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task 插件状态与消息归属落库后能读回()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "翻译会话",
            UpdatedAt = DateTime.Now,
            OriginMountId = "translate/sidebar",
            ActiveMountId = "translate/composer",
            LastPluginId = "translate"
        };

        session.PluginMemory["translate/composer/target"] = "ja";
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "hello"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "こんにちは")
        {
            Plugin = new PluginMessageInfo(
                "translate",
                "composer",
                "translate",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" },
                null)
        });

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("translate/sidebar", loaded.OriginMountId);
        Assert.Equal("translate/composer", loaded.ActiveMountId);
        Assert.Equal("translate", loaded.LastPluginId);
        Assert.Equal("ja", loaded.PluginMemory["translate/composer/target"]);

        Assert.Null(loaded.Messages[0].Plugin);
        var reply = loaded.Messages[1];
        Assert.NotNull(reply.Plugin);
        Assert.Equal("translate", reply.Plugin!.PluginId);
        Assert.Equal("composer", reply.Plugin.MountId);
        Assert.Equal("ja", reply.Plugin.Values["target"]);
        Assert.False(reply.Plugin.HasSegments);
    }

    [Fact]
    public async Task 派生卡片落库后能读回_运行中的收尾成失败()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "派生卡片",
            UpdatedAt = DateTime.Now
        };

        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" };
        var reply = new ChatMessage(ChatMessageRole.Assistant, "原文");
        reply.Derived.Add(new DerivedArtifact(
            "a1", "translate", "bubble", "translate", values, null, "译文",
            DerivedState.Done, null, "mock-model", 1200, 30, 8, DateTime.Now));
        reply.Derived.Add(new DerivedArtifact(
            "a2", "translate", "bubble", "translate", values, null, string.Empty,
            DerivedState.Running, null, "mock-model", 0, 0, 0, DateTime.Now));

        session.Messages.Add(reply);
        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        var derived = Assert.Single(loaded.Messages).Derived;

        Assert.Equal(2, derived.Count);
        Assert.Equal("译文", derived[0].Text);
        Assert.Equal(DerivedState.Done, derived[0].State);
        Assert.Equal(1200, derived[0].DurationMs);
        Assert.Equal(30, derived[0].InputTokens);
        Assert.Equal("ja", derived[0].Values["target"]);

        // 上次没跑完的卡片加载时收尾成失败，避免界面上永远转圈。
        Assert.Equal(DerivedState.Failed, derived[1].State);
        Assert.Contains("未完成", derived[1].Error);
    }

    [Fact]
    public async Task 老库缺列时自动补齐_读写都不抛异常()
    {
        var databasePath = Path.Combine(_root, "history.db");
        CreateLegacyDatabase(databasePath);

        var store = new ChatStore(_root);
        var loaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("legacy", loaded.Id);
        Assert.Null(loaded.ActiveMountId);
        Assert.Empty(loaded.PluginMemory);
        Assert.Single(loaded.Messages);
        Assert.Null(loaded.Messages[0].Plugin);

        // 迁移之后，写入新列也要正常。
        loaded.ActiveMountId = "translate/composer";
        loaded.LastPluginId = "translate";
        await store.SaveSessionAsync(loaded);

        var reopened = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("translate/composer", reopened.ActiveMountId);
        Assert.Equal("translate", reopened.LastPluginId);

        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        var sessionColumns = ReadColumns(connection, "sessions");
        Assert.Contains("OriginMountId", sessionColumns);
        Assert.Contains("ActiveMountId", sessionColumns);
        Assert.Contains("LastPluginId", sessionColumns);
        Assert.Contains("PluginMemoryJson", sessionColumns);
        Assert.Contains("PluginJson", ReadColumns(connection, "messages"));
        Assert.Contains("DerivedJson", ReadColumns(connection, "messages"));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响测试结果。
        }
    }
}
