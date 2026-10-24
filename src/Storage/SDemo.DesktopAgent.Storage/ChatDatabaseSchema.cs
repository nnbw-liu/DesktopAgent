using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Data;

/// <summary>
/// 聊天数据库的版本化结构升级（R1.3）。
/// <para>
/// 新库：直接 <c>Migrate()</c>，由 <c>Migrations/</c> 里的迁移建表。
/// 老库（EnsureCreated 时代创建、没有 <c>__EFMigrationsHistory</c>）：先按旧的
/// <see cref="LegacyColumns"/> 补列，让结构对齐 <c>InitialCreate</c>，再把这一条记为已应用（基线化），
/// 之后 <c>Migrate()</c> 只执行真正新增的迁移。
/// </para>
/// <para>
/// FTS5 表是有意的例外：<c>CREATE VIRTUAL TABLE</c> 在 EF 迁移里无法优雅降级，
/// 仍由 <see cref="Services.ArchiveSchema"/> 在迁移之后按需创建（缺失 FTS5 只影响知识库）。
/// </para>
/// </summary>
internal static class ChatDatabaseSchema
{
    /// <summary>
    /// EnsureCreated 时代的列清单：老库缺少这些列时先补上，再基线化到 InitialCreate。
    /// 新增迁移只改 <c>Migrations/</c>，不要再往这里加列。
    /// </summary>
    private static readonly (string Table, (string Name, string Definition)[] Columns)[] LegacyColumns =
    [
        ("sessions",
        [
            ("OriginMountId", "TEXT NULL"),
            ("ActiveMountId", "TEXT NULL"),
            ("LastPluginId", "TEXT NULL"),
            ("PluginMemoryJson", "TEXT NULL"),
            ("Summary", "TEXT NULL"),
            ("SummaryThroughOrder", "INTEGER NOT NULL DEFAULT 0"),
            ("SummaryUpdatedAt", "TEXT NULL"),
            ("SummaryTokens", "INTEGER NOT NULL DEFAULT 0"),
            ("LastVerificationJson", "TEXT NULL"),
            ("TodosJson", "TEXT NULL"),
            ("IsArchived", "INTEGER NOT NULL DEFAULT 0")
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
            ("MemoryCitationsJson", "TEXT NULL"),
            ("IsPinned", "INTEGER NOT NULL DEFAULT 0")
        ])
    ];

    /// <summary>迁移历史表；EF 用它判断哪些迁移已经应用。</summary>
    public const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// 打开库时调用：必要时基线化老库，执行未应用的迁移，最后保证 FTS5 索引存在。
    /// </summary>
    public static async Task PrepareAsync(ChatDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!ArchiveSchema.TableExists(connection, HistoryTable))
            {
                await BaselineLegacyAsync(db, connection, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        // M3：知识库 FTS5 表不参与 EF 模型；迁移之后按需补建（老库、新库都走这里）。
        await EnsureArchiveIndexAsync(db, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 没有迁移历史时判断是不是老库：两张表都在才补列 + 基线化。
    /// 表不全时不做任何事，交给 <c>Migrate()</c> 报出明确错误（或按新库建表）。
    /// </summary>
    private static async Task BaselineLegacyAsync(
        ChatDbContext db,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (!ArchiveSchema.TableExists(connection, "sessions")
            || !ArchiveSchema.TableExists(connection, "messages"))
        {
            return;
        }

        foreach (var (table, columns) in LegacyColumns)
        {
            await EnsureColumnsAsync(connection, table, columns, cancellationToken).ConfigureAwait(false);
        }

        var initial = db.Database.GetMigrations().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(initial))
        {
            // 没有迁移文件（例如裁剪过的构建）：保持老库可用，不再继续迁移。
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {HistoryTable} (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            INSERT OR IGNORE INTO {HistoryTable} ("MigrationId", "ProductVersion")
            VALUES ($id, $version);
            """;
        AddParameter(command, "$id", initial);
        AddParameter(command, "$version", ProductVersion());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureArchiveIndexAsync(ChatDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;

        if (opened)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (connection is SqliteConnection sqlite
                && !ArchiveSchema.TableExists(sqlite, ArchiveSchema.FtsTable))
            {
                ArchiveSchema.TryEnsure(sqlite);
            }
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task EnsureColumnsAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<(string Name, string Definition)> requiredColumns,
        CancellationToken cancellationToken)
    {
        var existing = await ReadColumnsAsync(connection, table, cancellationToken).ConfigureAwait(false);

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
                await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>迁移历史里记录的 EF 运行时版本（仅诊断用）。</summary>
    private static string ProductVersion() =>
        typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
}
