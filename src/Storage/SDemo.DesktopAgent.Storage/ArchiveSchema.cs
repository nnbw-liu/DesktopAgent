using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 工作区知识库的 SQLite 结构（M3）。FTS5 表不参与 EF 模型，由这里按需创建；
/// FTS5 不可用时只影响知识库，不影响会话读写。
/// </summary>
internal static class ArchiveSchema
{
    public const string FtsTable = "archived_memory_fts";

    public const string CreateFtsSql = $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTable} USING fts5(
            session_id UNINDEXED,
            title UNINDEXED,
            summary UNINDEXED,
            body UNINDEXED,
            terms,
            updated_at UNINDEXED,
            message_count UNINDEXED
        )
        """;

    /// <summary>建 FTS 表；已存在时是空操作。FTS5 模块缺失会抛 <see cref="SqliteException"/>。</summary>
    public static void Ensure(SqliteConnection connection)
    {
        TryEnsure(connection);
    }

    public static bool TryEnsure(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = CreateFtsSql;
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            // 某些精简构建没有 FTS5：知识库降级为不可用，不影响聊天。
            Debug.WriteLine($"创建工作区知识库索引失败：{ex.Message}");
            return false;
        }
    }

    public static bool TableExists(DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = $name LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        return command.ExecuteScalar() is not null;
    }

    public static bool ColumnExists(DbConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $name LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = column;
        command.Parameters.Add(parameter);

        return command.ExecuteScalar() is not null;
    }
}
