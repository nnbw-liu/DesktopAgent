using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Data;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 会话持久化：每个工作区一个 SQLite 文件，历史聊天（不归属工作区）单独一个文件。
/// 文件位置：%APPDATA%\SDemo\DesktopAgent\chats\
///   ├─ history.db
///   └─ workspaces\{workspaceId}.db
/// 所有写操作串行执行，避免同一时刻多个 SQLite 写入互相锁库。
/// </summary>
public sealed class ChatStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ChatStore(IAppPaths paths)
        : this(paths.ChatsRoot)
    {
    }

    public ChatStore(string root)
    {
        _root = root;
    }

    public string HistoryDatabasePath => Path.Combine(_root, "history.db");

    public string GetWorkspaceDatabasePath(string workspaceId) =>
        Path.Combine(_root, "workspaces", workspaceId + ".db");

    /// <summary>加载全部会话：历史聊天 DB + 每个工作区 DB。某个库损坏时跳过它，不影响其他库。</summary>
    public async Task<IReadOnlyList<ChatSession>> LoadAllAsync(
        IReadOnlyList<Workspace> workspaces,
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<ChatSession>();

        var historySessions = await LoadFileAsync(HistoryDatabasePath, workspaceId: null, cancellationToken);
        var knownWorkspaceIds = workspaces
            .Select(workspace => workspace.Id)
            .ToHashSet(StringComparer.Ordinal);

        // 工作区被手工从 settings.json 删掉时，历史库里的旧 WorkspaceId 视为已失效，回到历史聊天。
        foreach (var session in historySessions)
        {
            if (!string.IsNullOrEmpty(session.WorkspaceId) && !knownWorkspaceIds.Contains(session.WorkspaceId))
            {
                session.WorkspaceId = null;
            }
        }

        sessions.AddRange(historySessions);

        foreach (var workspace in workspaces)
        {
            var path = GetWorkspaceDatabasePath(workspace.Id);
            sessions.AddRange(await LoadFileAsync(path, workspace.Id, cancellationToken));
        }

        return sessions;
    }

    public async Task SaveSessionAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(session, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteSessionAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await DeleteCoreAsync(session.Id, ResolveDatabasePath(session.WorkspaceId), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>把会话从一个工作区（或历史聊天）移动到另一个库。</summary>
    public async Task MoveSessionAsync(
        ChatSession session,
        string? targetWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolveDatabasePath(session.WorkspaceId);
        var targetPath = ResolveDatabasePath(targetWorkspaceId);

        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            await SaveSessionAsync(session, cancellationToken);
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // 先写目标库再删源库，中途失败最多留下重复数据，不会丢会话。
            session.WorkspaceId = targetWorkspaceId;
            await SaveCoreAsync(session, cancellationToken);
            await DeleteCoreAsync(session.Id, sourcePath, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 工作区被删除：把该工作区 DB 里的会话全部搬到历史聊天 DB，然后删掉工作区 DB 文件。
    /// </summary>
    public async Task DeleteWorkspaceStoreAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var path = GetWorkspaceDatabasePath(workspaceId);
            if (!File.Exists(path))
            {
                return;
            }

            var sessions = await LoadFileCoreAsync(path, workspaceId, cancellationToken);
            foreach (var session in sessions)
            {
                session.WorkspaceId = null;
                await SaveCoreAsync(session, cancellationToken);
            }

            // Microsoft.Data.Sqlite 默认启用连接池，先清池再删文件，否则文件会一直被占用。
            SqliteConnection.ClearAllPools();

            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"删除工作区数据库失败：{ex.Message}");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string ResolveDatabasePath(string? workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId)
            ? HistoryDatabasePath
            : GetWorkspaceDatabasePath(workspaceId);

    private async Task<IReadOnlyList<ChatSession>> LoadFileAsync(
        string path,
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return await LoadFileCoreAsync(path, workspaceId, cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            // 某个库损坏 / 被占用时不让整个应用起不来，其他库照常加载。
            Debug.WriteLine($"读取聊天数据库失败（{path}）：{ex.Message}");
            return [];
        }
    }

    /// <summary>读取时不做容错：用于“删工作区前迁移会话”，读失败就不要继续删库。</summary>
    private static async Task<IReadOnlyList<ChatSession>> LoadFileCoreAsync(
        string path,
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        await using var db = await OpenDatabaseAsync(path, cancellationToken);
        var records = await db.Sessions
            .AsNoTracking()
            .Include(record => record.Messages)
            .OrderByDescending(record => record.UpdatedAt)
            .ToListAsync(cancellationToken);

        return records.Select(record => ToSession(record, workspaceId)).ToList();
    }

    private async Task SaveCoreAsync(ChatSession session, CancellationToken cancellationToken)
    {
        var path = ResolveDatabasePath(session.WorkspaceId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var db = await OpenDatabaseAsync(path, cancellationToken);

        var record = await db.Sessions
            .Include(item => item.Messages)
            .FirstOrDefaultAsync(item => item.Id == session.Id, cancellationToken);

        if (record is null)
        {
            record = new SessionRecord { Id = session.Id };
            db.Sessions.Add(record);
        }

        record.Title = session.Title;
        record.Meta = session.Meta;
        record.WorkspaceId = session.WorkspaceId;
        record.IsPinned = session.IsPinned;
        record.UpdatedAt = session.UpdatedAt;
        record.OriginMountId = session.OriginMountId;
        record.ActiveMountId = session.ActiveMountId;
        record.LastPluginId = session.LastPluginId;
        record.PluginMemoryJson = session.PluginMemory.Count == 0
            ? null
            : JsonSerializer.Serialize(session.PluginMemory, JsonOptions);

        // 摘要覆盖范围超出当前消息数（删过消息 / 手工改库）：视为过期，归零后下次重算。
        if (session.SummaryThroughOrder < 0 || session.SummaryThroughOrder > session.Messages.Count)
        {
            session.SummaryThroughOrder = 0;
            session.Summary = null;
            session.SummaryTokens = 0;
            session.SummaryUpdatedAt = null;
            session.SummaryIsFallback = false;
        }

        record.Summary = session.HasSummary ? session.Summary : null;
        record.SummaryThroughOrder = session.HasSummary ? session.SummaryThroughOrder : 0;
        record.SummaryTokens = session.HasSummary ? session.SummaryTokens : 0;
        record.SummaryUpdatedAt = session.HasSummary ? session.SummaryUpdatedAt : null;
        record.LastVerificationJson = session.LastVerification is null
            ? null
            : JsonSerializer.Serialize(session.LastVerification, JsonOptions);
        record.TodosJson = session.Todos.Count == 0
            ? null
            : JsonSerializer.Serialize(session.Todos, JsonOptions);

        var existing = record.Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;

        foreach (var message in session.Messages)
        {
            if (!existing.TryGetValue(message.Id, out var messageRecord))
            {
                messageRecord = new MessageRecord
                {
                    Id = message.Id,
                    SessionId = session.Id
                };

                record.Messages.Add(messageRecord);
            }

            messageRecord.Order = order++;
            messageRecord.Role = message.Role;
            messageRecord.Content = message.Content;
            messageRecord.Reasoning = message.Reasoning;
            messageRecord.CreatedAt = message.CreatedAt;
            messageRecord.DurationMs = message.DurationMs;
            messageRecord.InputTokens = message.InputTokens;
            messageRecord.OutputTokens = message.OutputTokens;
            messageRecord.AttachmentsJson = message.Attachments.Count == 0
                ? null
                : JsonSerializer.Serialize(message.Attachments, JsonOptions);
            messageRecord.ToolCallsJson = message.ToolCalls.Count == 0
                ? null
                : JsonSerializer.Serialize(message.ToolCalls, JsonOptions);
            messageRecord.PluginJson = message.Plugin is null
                ? null
                : JsonSerializer.Serialize(message.Plugin, JsonOptions);
            messageRecord.DerivedJson = message.Derived.Count == 0
                ? null
                : JsonSerializer.Serialize(message.Derived, JsonOptions);
            messageRecord.ToolTraceJson = message.ToolTrace.Count == 0
                ? null
                : JsonSerializer.Serialize(message.ToolTrace, JsonOptions);
            messageRecord.IsPinned = message.IsPinned;
            keep.Add(message.Id);
        }

        // 会话里删掉的消息（目前还没有删除入口，先保持同步逻辑完整）。
        foreach (var orphan in record.Messages.Where(message => !keep.Contains(message.Id)).ToList())
        {
            record.Messages.Remove(orphan);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteCoreAsync(string sessionId, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using var db = CreateContext(path);
        var record = await db.Sessions.FirstOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (record is null)
        {
            return;
        }

        db.Sessions.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ChatDbContext CreateContext(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new ChatDbContext(options);
    }

    /// <summary>打开数据库并保证表结构是最新的（含轻量迁移）。</summary>
    private static async Task<ChatDbContext> OpenDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        var db = CreateContext(path);

        try
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            await UpgradeSchemaAsync(db, cancellationToken);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// 轻量迁移：老库缺列时自动补上，避免升级后直接报 “no such column”。
    /// 两张表各自维护列清单——新增列时只加在这里，不要再写「只扫 messages」的旧逻辑。
    /// （表结构大改仍建议改用 EF 迁移。）
    /// </summary>
    private static readonly (string Table, (string Name, string Definition)[] Columns)[] RequiredColumns =
    [
        ("sessions",
        [
            ("OriginMountId", "TEXT NULL"),
            ("ActiveMountId", "TEXT NULL"),
            ("LastPluginId", "TEXT NULL"),
            ("PluginMemoryJson", "TEXT NULL"),
            // P1 建列、P2 写入；提前建好可以避免两期各改一次迁移。
            ("Summary", "TEXT NULL"),
            ("SummaryThroughOrder", "INTEGER NOT NULL DEFAULT 0"),
            ("SummaryUpdatedAt", "TEXT NULL"),
            ("SummaryTokens", "INTEGER NOT NULL DEFAULT 0"),
            ("LastVerificationJson", "TEXT NULL"),
            ("TodosJson", "TEXT NULL")
        ]),
        ("messages",
        [
            ("AttachmentsJson", "TEXT NULL"),
            ("CreatedAt", "TEXT NULL"),
            ("DurationMs", "INTEGER NOT NULL DEFAULT 0"),
            ("InputTokens", "INTEGER NOT NULL DEFAULT 0"),
            ("OutputTokens", "INTEGER NOT NULL DEFAULT 0"),
            ("ToolCallsJson", "TEXT NULL"),
            ("PluginJson", "TEXT NULL"),
            ("DerivedJson", "TEXT NULL"),
            ("ToolTraceJson", "TEXT NULL"),
            ("IsPinned", "INTEGER NOT NULL DEFAULT 0")
        ])
    ];

    private static async Task UpgradeSchemaAsync(ChatDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            foreach (var (table, columns) in RequiredColumns)
            {
                await EnsureColumnsAsync(connection, table, columns, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureColumnsAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<(string Name, string Definition)> requiredColumns,
        CancellationToken cancellationToken)
    {
        var existing = await ReadColumnsAsync(connection, table, cancellationToken);

        // 表还不存在（空库 / 损坏库）时不在这里硬改，交给后续操作报明确错误。
        if (existing.Count == 0)
        {
            return;
        }

        foreach (var column in requiredColumns)
        {
            if (existing.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column.Name} {column.Definition}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
                existing.Add(column.Name);
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // 另一个进程刚好也补过这一列，忽略。
            }
        }
    }

    private static async Task<List<string>> ReadColumnsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static List<ChatAttachment> DeserializeAttachments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChatAttachment>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, int> DeserializeToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions)
                   ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> DeserializePluginMemory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var memory = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return memory is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(memory, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static PluginMessageInfo? DeserializePlugin(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluginMessageInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // 历史数据的插件归属读不出来时降级成普通消息，不影响会话打开。
            return null;
        }
    }

    private static List<DerivedArtifact> DeserializeDerived(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var artifacts = JsonSerializer.Deserialize<List<DerivedArtifact>>(json, JsonOptions);
            if (artifacts is null)
            {
                return [];
            }

            // 上次运行没结束（应用被关掉 / 崩溃）的卡片：加载时收尾成失败，避免永远转圈。
            return artifacts
                .Select(artifact => artifact.State == DerivedState.Running
                    ? artifact with
                    {
                        State = DerivedState.Failed,
                        Error = artifact.Error ?? "上次运行未完成"
                    }
                    : artifact)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>工具轨迹：坏数据降级成空列表；超过上限的只保留最近的部分。</summary>
    private static List<ToolTraceEntry> DeserializeToolTrace(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<ToolTraceEntry>>(json, JsonOptions);
            if (entries is null)
            {
                return [];
            }

            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ToolId))
                .TakeLast(AgentRunMetrics.MaxStoredEntries)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>最近一次验证结果；坏数据降级为 null。</summary>
    private static List<TodoItem> DeserializeTodos(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOptions)?
                .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                .Take(TodoItem.MaxItems)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static VerificationRecord? DeserializeVerification(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VerificationRecord>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatSession ToSession(SessionRecord record, string? workspaceId)
    {
        var session = new ChatSession
        {
            Id = record.Id,
            Title = record.Title,
            Meta = record.Meta,
            WorkspaceId = workspaceId ?? record.WorkspaceId,
            IsPinned = record.IsPinned,
            UpdatedAt = record.UpdatedAt,
            OriginMountId = record.OriginMountId,
            ActiveMountId = record.ActiveMountId,
            LastPluginId = record.LastPluginId,
            PluginMemory = DeserializePluginMemory(record.PluginMemoryJson),
            Summary = record.Summary,
            SummaryThroughOrder = record.SummaryThroughOrder,
            SummaryUpdatedAt = record.SummaryUpdatedAt,
            SummaryTokens = record.SummaryTokens,
            SummaryIsFallback = SummaryFormat.IsFallback(record.Summary),
            LastVerification = DeserializeVerification(record.LastVerificationJson),
            Todos = DeserializeTodos(record.TodosJson),
            Messages = record.Messages
                .OrderBy(message => message.Order)
                .Select(message => new ChatMessage(message.Role, message.Content)
                {
                    Id = message.Id,
                    Reasoning = message.Reasoning,
                    Attachments = DeserializeAttachments(message.AttachmentsJson),
                    CreatedAt = message.CreatedAt ?? record.UpdatedAt,
                    DurationMs = message.DurationMs,
                    InputTokens = message.InputTokens,
                    OutputTokens = message.OutputTokens,
                    ToolCalls = DeserializeToolCalls(message.ToolCallsJson),
                    Plugin = DeserializePlugin(message.PluginJson),
                    Derived = DeserializeDerived(message.DerivedJson),
                    ToolTrace = DeserializeToolTrace(message.ToolTraceJson),
                    IsPinned = message.IsPinned
                })
                .ToList()
        };

        // 库被手工改过 / 消息被删过：摘要覆盖范围大于消息数时视为无效，清空后下次重算。
        if (session.SummaryThroughOrder < 0 || session.SummaryThroughOrder > session.Messages.Count)
        {
            session.Summary = null;
            session.SummaryThroughOrder = 0;
            session.SummaryTokens = 0;
            session.SummaryUpdatedAt = null;
            session.SummaryIsFallback = false;
        }

        return session;
    }
}
