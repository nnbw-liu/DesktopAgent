namespace SDemo.DesktopAgent.Services;

/// <summary>一条检索命中（M3）：会话卡 + 摘要片段。</summary>
public sealed record MemorySearchHit(
    string SessionId,
    string Title,
    DateTime UpdatedAt,
    string Snippet);

/// <summary>归档列表里的一条会话。</summary>
public sealed record MemorySessionCard(
    string SessionId,
    string Title,
    DateTime UpdatedAt,
    int MessageCount);

/// <summary>一次检索的结果；<paramref name="Widened"/> 表示 AND 无命中后退化为 OR。</summary>
public sealed record MemorySearchResult(
    string Query,
    IReadOnlyList<MemorySearchHit> Hits,
    bool Widened,
    int ArchivedCount)
{
    public static MemorySearchResult Empty(string query, int archivedCount = 0) =>
        new(query, [], false, archivedCount);
}

/// <summary>
/// 读取归档会话正文的一页。<paramref name="NextOffset"/> 为 0 表示已经读完。
/// </summary>
public sealed record MemoryReadResult(
    bool Found,
    string SessionId,
    string Title,
    DateTime UpdatedAt,
    string Text,
    int NextOffset);

/// <summary>
/// 工作区知识库契约（M3 §4.6）：只依赖会话库，不依赖模型 / UI。
/// 实现由 Storage 层提供（SQLite + FTS5）；工具包与上下文层只依赖这个接口。
/// </summary>
public interface IWorkspaceMemory
{
    /// <summary>当前工作区是否有可检索的归档会话。</summary>
    bool IsAvailable(string? workspaceId);

    /// <summary>当前工作区已归档会话数；知识库关闭 / 无工作区 / 无库时为 0。</summary>
    int CountArchived(string? workspaceId);

    /// <summary>归档会话列表（按更新时间倒序）。</summary>
    IReadOnlyList<MemorySessionCard> ListArchived(string? workspaceId);

    /// <summary>按关键词检索归档会话；<paramref name="topK"/> 由实现夹到安全范围。</summary>
    MemorySearchResult Search(string? workspaceId, string query, int topK);

    /// <summary>读取一条归档会话的正文；<paramref name="maxChars"/> 为单页字符上限。</summary>
    MemoryReadResult Read(
        string? workspaceId,
        string sessionId,
        string? query,
        int offset,
        int maxChars);

    /// <summary>（重新）索引一条会话；会话未归档 / 不存在时等价于删除索引行。</summary>
    void Index(string? workspaceId, string sessionId);

    /// <summary>删除一条会话的索引行（取消归档 / 删除会话时）。</summary>
    void Remove(string? workspaceId, string sessionId);

    /// <summary>重建当前工作区的全部归档索引。</summary>
    void Rebuild(string? workspaceId);
}
