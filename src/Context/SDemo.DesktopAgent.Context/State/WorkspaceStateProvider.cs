using System.IO;
using System.Security.Cryptography;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 工作记忆状态包的数据源（P2 §3.3）：树扫描 30 秒缓存、最近改动来自会话工具轨迹、
/// 最近动作取轨迹回放行。每次请求重建，但不会每轮扫盘。
/// </summary>
public sealed class WorkspaceStateProvider
{
    /// <summary>树的最大条目数（与计划一致）。</summary>
    public const int MaxTreeEntries = 120;

    /// <summary>树的最大深度。</summary>
    public const int TreeDepth = 2;

    /// <summary>最近改动的文件数上限。</summary>
    public const int MaxRecentEdits = 8;

    /// <summary>最近动作的条数上限。</summary>
    public const int MaxRecentActions = 12;

    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "apply_patch", "edit_file", "create_file",
        "replace_in_file", "move_file", "delete_file", "patch_file"
    };

    private readonly IWorkspaceTreeScanner _scanner;
    private readonly TimeProvider _time;
    private readonly TimeSpan _treeTtl;
    private readonly TimeSpan _hashTtl;

    private string? _cachedRoot;
    private IReadOnlyList<string>? _cachedTree;
    private DateTimeOffset _cachedAt;

    private readonly Dictionary<string, (string? Hash, DateTimeOffset At)> _hashes =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceStateProvider(
        IWorkspaceTreeScanner? scanner = null,
        TimeProvider? time = null,
        TimeSpan? treeTtl = null,
        TimeSpan? hashTtl = null)
    {
        _scanner = scanner ?? new FileSystemWorkspaceTreeScanner();
        _time = time ?? TimeProvider.System;
        _treeTtl = treeTtl ?? TimeSpan.FromSeconds(30);
        _hashTtl = hashTtl ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>树的条目数（测试缓存用）。</summary>
    public int TreeScans { get; private set; }

    /// <summary>拼一个状态包；<paramref name="session"/> 为空时只给工作区结构。</summary>
    /// <param name="includeTree">
    /// 关掉「工作区结构」段时不必扫盘（P2a：验证 / 计划 / 策略仍然要），
    /// 于是树只留空列表，其余事实照常给。
    /// </param>
    public WorkspaceState Build(string? root, ChatSession? session, bool includeTree = true)
    {
        var safeRoot = string.IsNullOrWhiteSpace(root) ? string.Empty : root;
        var tree = !includeTree || string.IsNullOrEmpty(safeRoot)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : GetTree(safeRoot);
        var edits = session is null
            ? (IReadOnlyList<RecentEdit>)Array.Empty<RecentEdit>()
            : CollectEdits(safeRoot, session);
        var actions = session is null
            ? (IReadOnlyList<RecentAction>)Array.Empty<RecentAction>()
            : CollectActions(session);

        return new WorkspaceState(
            safeRoot,
            tree,
            edits,
            actions,
            LastVerification: session?.LastVerification?.Format(),
            PinnedCount: session?.Messages.Count(message => message.IsPinned) ?? 0,
            Todos: session?.Todos);
    }

    private IReadOnlyList<string> GetTree(string root)
    {
        var now = _time.GetUtcNow();

        if (_cachedTree is not null
            && string.Equals(_cachedRoot, root, StringComparison.OrdinalIgnoreCase)
            && now - _cachedAt < _treeTtl)
        {
            return _cachedTree;
        }

        var tree = _scanner.Scan(root, TreeDepth, MaxTreeEntries);
        TreeScans++;
        _cachedRoot = root;
        _cachedTree = tree;
        _cachedAt = now;

        return tree;
    }

    private List<RecentEdit> CollectEdits(string root, ChatSession session)
    {
        var byPath = new Dictionary<string, RecentEdit>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in session.Messages)
        {
            foreach (var trace in message.ToolTrace)
            {
                if (!WriteTools.Contains(trace.ToolId))
                {
                    continue;
                }

                var path = ResolvePath(trace.ArgsDigest);
                if (path is null)
                {
                    continue;
                }

                byPath[path] = new RecentEdit(path, HashPrefix(root, path), trace.At, trace.ToolId);
            }
        }

        return byPath.Values
            .OrderByDescending(edit => edit.At)
            .Take(MaxRecentEdits)
            .ToList();
    }

    private static List<RecentAction> CollectActions(ChatSession session)
    {
        var actions = session.Messages
            .SelectMany(message => message.ToolTrace)
            .OrderBy(trace => trace.At)
            .TakeLast(MaxRecentActions)
            .Select(trace => new RecentAction(trace.At, ToolTraceFormatter.RenderLine(trace)))
            .ToList();

        return actions;
    }

    private string? HashPrefix(string root, string path)
    {
        var fullPath = Path.IsPathRooted(path)
            ? path
            : string.IsNullOrEmpty(root) ? path : Path.Combine(root, path);

        var now = _time.GetUtcNow();

        if (_hashes.TryGetValue(fullPath, out var cached) && now - cached.At < _hashTtl)
        {
            return cached.Hash;
        }

        string? hash = null;

        try
        {
            if (File.Exists(fullPath))
            {
                using var stream = File.OpenRead(fullPath);
                var buffer = new byte[Math.Min(64 * 1024, (int)Math.Min(stream.Length, 64 * 1024))];
                var read = stream.Read(buffer, 0, buffer.Length);

                if (read > 0)
                {
                    var digest = SHA256.HashData(buffer.AsSpan(0, read));
                    hash = Convert.ToHexString(digest)[..4].ToLowerInvariant();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hash = null;
        }

        _hashes[fullPath] = (hash, now);
        return hash;
    }

    private static string? ResolvePath(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var path = digest.Trim();

        // 没有路径参数时 ArgsDigest 是 sha256:…，不能当文件路径。
        return path.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? null : path;
    }
}
