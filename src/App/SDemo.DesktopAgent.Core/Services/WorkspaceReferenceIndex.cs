using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Core.Services;

/// <summary>
/// <c>@</c> 补全的工作区路径索引：广度优先枚举、排除构建与依赖目录、按 30 秒缓存。
/// <para>
/// 只返回路径，不读内容——引用一个文件应该只花几个 token，模型仍要 <c>read_file</c> 才能看到它。
/// </para>
/// </summary>
public sealed class WorkspaceReferenceIndex : IComposerReferenceIndex
{
    /// <summary>一次最多索引多少条（防止主目录 / 巨型仓库把输入框拖慢）。</summary>
    public const int MaxEntries = 4000;

    /// <summary>枚举深度上限。</summary>
    public const int MaxDepth = 8;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private string? _root;
    private DateTimeOffset _expiresAt;
    private IReadOnlyList<string> _entries = [];

    /// <summary>生产用：以系统时钟为准。</summary>
    public WorkspaceReferenceIndex()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>测试用：可注入时钟，验证缓存过期。</summary>
    public WorkspaceReferenceIndex(Func<DateTimeOffset> clock)
    {
        _clock = clock;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return [];
        }

        var normalized = Path.GetFullPath(rootPath);
        var now = _clock();

        lock (_gate)
        {
            if (_root == normalized && now < _expiresAt)
            {
                return _entries;
            }

            _root = normalized;
            _expiresAt = now + CacheLifetime;
            _entries = Enumerate(normalized);

            return _entries;
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
        {
            _root = null;
            _entries = [];
            _expiresAt = default;
        }
    }

    private static IReadOnlyList<string> Enumerate(string root)
    {
        var results = new List<string>(256);
        var queue = new Queue<(string Path, string Relative, int Depth)>();
        queue.Enqueue((root, string.Empty, 1));

        while (queue.Count > 0 && results.Count < MaxEntries)
        {
            var (directory, relative, depth) = queue.Dequeue();

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
                    .Where(name => name is not null && !IsExcluded(name))
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
                if (results.Count >= MaxEntries)
                {
                    break;
                }

                results.Add($"{relative}{name}/");

                if (depth < MaxDepth)
                {
                    queue.Enqueue((Path.Combine(directory, name), $"{relative}{name}/", depth + 1));
                }
            }

            foreach (var name in files)
            {
                if (results.Count >= MaxEntries)
                {
                    break;
                }

                results.Add($"{relative}{name}");
            }
        }

        return results;
    }

    private static bool IsExcluded(string name) => WorkspaceExcludes.Contains(name);
}
