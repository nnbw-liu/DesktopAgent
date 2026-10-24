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
    public async Task 步骤时间线落库后能按原顺序读回()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "带步骤的会话",
            UpdatedAt = DateTime.Now
        };

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "看看 README"));

        var reply = new ChatMessage(ChatMessageRole.Assistant, "看完了")
        {
            Reasoning = "先看目录。再看文件。",
            DurationMs = 4200
        };

        reply.ToolTrace.Add(new ToolTraceEntry(
            "read_file",
            ToolTraceEntry.StatusOk,
            null,
            "README.md",
            "读取 120 行",
            3200,
            DateTime.Now,
            ReasoningChars: 5,
            TextChars: 0,
            CallId: "call-1",
            ArgsJson: "{\"path\":\"README.md\"}",
            Title: "README.md",
            DurationMs: 120,
            ResultPreview: "# SDemo"));

        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "先看目录。", StepIndex: 0));
        reply.Steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, 0, 0, "call-1"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "再看文件。", StepIndex: 0));
        reply.Steps.Add(new ChatStep(ChatStepKind.Text, "看完了", StepIndex: 0));
        session.Messages.Add(reply);

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        var steps = loaded.Messages[1].Steps;

        Assert.Equal(
            [ChatStepKind.Reasoning, ChatStepKind.Tool, ChatStepKind.Reasoning, ChatStepKind.Text],
            steps.Select(step => step.Kind));
        Assert.Equal("call-1", steps[1].CallId);
        Assert.Equal(ChatGroupPart.Answer, steps[3].Part);
        Assert.Equal("README.md", loaded.Messages[1].ToolTrace[0].Title);
        Assert.Equal(120, loaded.Messages[1].ToolTrace[0].DurationMs);
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
        loaded.Todos = [new TodoItem("补列后的计划", TodoStatus.InProgress)];
        await store.SaveSessionAsync(loaded);

        var reopened = Assert.Single(await store.LoadAllAsync([legacyWorkspace]));
        Assert.True(reopened.Messages[0].IsPinned);
        Assert.Single(reopened.Messages[0].ToolTrace);
        Assert.Equal("补列后的计划", Assert.Single(reopened.Todos).Content);

        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        var sessionColumns = ReadColumns(connection, "sessions");
        Assert.Contains("Summary", sessionColumns);
        Assert.Contains("SummaryThroughOrder", sessionColumns);
        Assert.Contains("SummaryUpdatedAt", sessionColumns);
        Assert.Contains("SummaryTokens", sessionColumns);
        Assert.Contains("LastVerificationJson", sessionColumns);
        Assert.Contains("TodosJson", sessionColumns);
        Assert.Contains("IsArchived", sessionColumns);
        Assert.Contains("ScopedSettingsJson", sessionColumns);

        var messageColumns = ReadColumns(connection, "messages");
        Assert.Contains("ToolTraceJson", messageColumns);
        Assert.Contains("MemoryCitationsJson", messageColumns);
        Assert.Contains("IsPinned", messageColumns);
    }

    [Fact]
    public async Task 聊天室作用域设置落库后能读回()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "带作用域设置的会话",
            UpdatedAt = DateTime.Now,
            ScopedSettings = new ScopedSettings
            {
                ProviderId = "local",
                ModelId = "qwen3.5-4b",
                BudgetRatio = 0.6,
                AutoCompact = false,
                AgentMode = AgentModePreference.Compact,
                ConfirmFileWrite = false
            }
        };

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "测试作用域设置"));

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.Equal("local", loaded.ScopedSettings.ProviderId);
        Assert.Equal("qwen3.5-4b", loaded.ScopedSettings.ModelId);
        Assert.Equal(0.6, loaded.ScopedSettings.BudgetRatio);
        Assert.False(loaded.ScopedSettings.AutoCompact);
        Assert.Equal(AgentModePreference.Compact, loaded.ScopedSettings.AgentMode);
        Assert.False(loaded.ScopedSettings.ConfirmFileWrite);
    }

    [Fact]
    public async Task 归档状态与知识库引用落库后能读回()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "已归档的会话",
            WorkspaceId = "w1",
            IsArchived = true,
            UpdatedAt = DateTime.Now
        };

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "上次沙箱 ACL 的结论"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "结论见原会话")
        {
            MemoryCitations =
            [
                new SDemo.DesktopAgent.Context.MemoryCitation("old-session", "沙箱隔离复盘", new DateTime(2026, 10, 20))
            ]
        });

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([new Workspace { Id = "w1", Name = "w", Path = _root }]));

        Assert.True(loaded.IsArchived);
        var citation = Assert.Single(loaded.Messages[1].MemoryCitations);
        Assert.Equal("old-session", citation.SessionId);
        Assert.Equal("沙箱隔离复盘", citation.Title);
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
