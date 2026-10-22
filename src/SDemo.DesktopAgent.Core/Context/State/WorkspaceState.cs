using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>工作区状态包（P2 §3.3）：全部由宿主事实拼装，不调用模型。</summary>
public sealed record WorkspaceState(
    string Root,
    IReadOnlyList<string> Tree,
    IReadOnlyList<RecentEdit> RecentEdits,
    IReadOnlyList<RecentAction> RecentActions,
    string? LastVerification,
    int PinnedCount);

/// <summary>最近改动的一个文件。</summary>
public sealed record RecentEdit(string Path, string? HashPrefix, DateTime At, string ToolId);

/// <summary>最近一次工具动作的回放行。</summary>
public sealed record RecentAction(DateTime At, string Line);

/// <summary>工作区树扫描器；抽出来是为了测试缓存与排除规则。</summary>
public interface IWorkspaceTreeScanner
{
    /// <summary>返回相对路径列表；目录以 <c>/</c> 结尾。按 BFS 顺序，目录在文件前。</summary>
    IReadOnlyList<string> Scan(string root, int depth, int maxEntries);
}

/// <summary>真实文件系统扫描：深度 2、最多 120 项、排除构建与依赖目录。</summary>
public sealed class FileSystemWorkspaceTreeScanner : IWorkspaceTreeScanner
{
    public IReadOnlyList<string> Scan(string root, int depth, int maxEntries)
    {
        var result = new List<string>();
        var maxDepth = Math.Max(1, depth);
        var limit = Math.Max(1, maxEntries);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return result;
        }

        var queue = new Queue<(string Directory, string Relative, int Depth)>();
        queue.Enqueue((root, string.Empty, 1));

        while (queue.Count > 0 && result.Count < limit)
        {
            var (directory, relative, currentDepth) = queue.Dequeue();
            List<string> directories;
            List<string> files;

            try
            {
                directories = Directory
                    .EnumerateDirectories(directory)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && !IsExcluded(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                files = Directory
                    .EnumerateFiles(directory)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var name in directories)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                result.Add($"{relative}{name}/");

                if (currentDepth < maxDepth)
                {
                    queue.Enqueue((Path.Combine(directory, name), $"{relative}{name}/", currentDepth + 1));
                }
            }

            foreach (var name in files)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                result.Add($"{relative}{name}");
            }
        }

        return result;
    }

    /// <summary>排除规则集中在这里，后续可配置（P2 先固定）。</summary>
    public static bool IsExcluded(string name) =>
        StatePackFormatter.DefaultExcludes.Contains(name);
}
