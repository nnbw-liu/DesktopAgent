using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 工具可以“只读访问”的附件目录：<c>{UploadsRoot}\{sessionId}</c>。
/// 会话 id 来自数据库 / UI，仍然当成不可信输入：带分隔符或 <c>..</c> 的一律不授予，
/// 目录不存在也不授予，避免把沙箱工具的可读范围扩大到别的目录。
/// </summary>
internal static class AgentAttachmentRoots
{
    public static IReadOnlyList<string> Build(IAppPaths paths, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || sessionId.Contains("..", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            var directory = Path.Combine(paths.UploadsRoot, sessionId);
            return Directory.Exists(directory) ? [Path.GetFullPath(directory)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }
}
