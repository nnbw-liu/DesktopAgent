using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>归档 / 取消归档的结果（M3）：界面只需要一句可展示的话。</summary>
public sealed record ArchiveOutcome(bool Changed, string Message);

/// <summary>
/// 归档编排（M3）：先改会话状态并落库，再维护工作区知识库索引。
/// 索引失败不回滚归档（会话已经保存），只说清楚「下次检索会重建」。
/// </summary>
public sealed class SessionArchiveService
{
    private readonly ChatStore _store;
    private readonly IWorkspaceMemory _memory;

    public SessionArchiveService(ChatStore store, IWorkspaceMemory memory)
    {
        _store = store;
        _memory = memory;
    }

    public async Task<ArchiveOutcome> ArchiveAsync(
        ChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspaceId))
        {
            return new ArchiveOutcome(false, "只有归属工作区的会话才能归档到知识库。");
        }

        if (session.Messages.Count == 0)
        {
            return new ArchiveOutcome(false, "空会话没有可归档的内容。");
        }

        if (session.IsArchived)
        {
            return new ArchiveOutcome(false, "这条会话已经在归档里。");
        }

        session.IsArchived = true;
        await _store.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);

        var warning = IndexSafely(session.WorkspaceId, session.Id);
        return new ArchiveOutcome(true, "已归档到工作区知识库。" + warning);
    }

    public async Task<ArchiveOutcome> UnarchiveAsync(
        ChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsArchived)
        {
            return new ArchiveOutcome(false, "这条会话没有归档。");
        }

        session.IsArchived = false;
        await _store.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);

        try
        {
            _memory.Remove(session.WorkspaceId, session.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ArchiveOutcome(true, $"已取消归档；索引清理失败：{ex.Message}");
        }

        return new ArchiveOutcome(true, "已取消归档。");
    }

    /// <summary>重建当前工作区的索引；返回一句结果说明。</summary>
    public string Rebuild(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return "当前会话没有归属工作区，无法重建知识库索引。";
        }

        try
        {
            _memory.Rebuild(workspaceId);
            return $"已重建索引：{_memory.CountArchived(workspaceId)} 条归档会话。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"重建索引失败：{ex.Message}";
        }
    }

    private string IndexSafely(string workspaceId, string sessionId)
    {
        try
        {
            _memory.Index(workspaceId, sessionId);
            return string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"索引将在下次检索时自动重建（本次索引失败：{ex.Message}）";
        }
    }
}
