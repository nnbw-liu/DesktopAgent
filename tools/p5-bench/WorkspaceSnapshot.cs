using System.Security.Cryptography;

namespace SDemo.P5Bench;

/// <summary>
/// 任务隔离（R1）：P5 的任务 A–E 会改同一批文件，串行跑会互相污染。
/// 这里在跑第一个任务前给 <c>src/</c> 与 <c>tests/</c> 做一份内容快照，
/// 每个任务开始前只把「被改过 / 新增 / 删除」的文件还原，保留 bin/obj（不触发冷构建）。
/// </summary>
internal sealed class WorkspaceSnapshot : IDisposable
{
    private static readonly string[] Roots = ["src", "tests"];

    private readonly string _workspace;
    private readonly string _backup;
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    private WorkspaceSnapshot(string workspace, string backup)
    {
        _workspace = workspace;
        _backup = backup;
    }

    public static WorkspaceSnapshot Create(string workspace)
    {
        var backup = Path.Combine(Path.GetTempPath(), "sdemo-p5-snapshot", Guid.NewGuid().ToString("N"));
        var snapshot = new WorkspaceSnapshot(Path.GetFullPath(workspace), backup);

        foreach (var file in snapshot.EnumerateWorkspaceFiles())
        {
            var relative = Path.GetRelativePath(snapshot._workspace, file);
            snapshot._hashes[relative] = Hash(file);

            var target = Path.Combine(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        return snapshot;
    }

    /// <summary>把工作区恢复成快照状态（只动必要文件）。</summary>
    public void Restore()
    {
        foreach (var file in EnumerateWorkspaceFiles().ToArray())
        {
            var relative = Path.GetRelativePath(_workspace, file);

            if (!_hashes.ContainsKey(relative))
            {
                // 任务新增的文件：删掉，避免影响下一个任务。
                File.Delete(file);
                continue;
            }

            if (!string.Equals(Hash(file), _hashes[relative], StringComparison.Ordinal))
            {
                var backup = Path.Combine(_backup, relative);
                if (File.Exists(backup))
                {
                    File.Copy(backup, file, overwrite: true);
                }
            }
        }

        // 补回被删掉的文件。
        foreach (var relative in _hashes.Keys)
        {
            var target = Path.Combine(_workspace, relative);
            if (!File.Exists(target))
            {
                var backup = Path.Combine(_backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
            }
        }
    }

    private IEnumerable<string> EnumerateWorkspaceFiles()
    {
        foreach (var root in Roots)
        {
            var directory = Path.Combine(_workspace, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_workspace, file);
                var segments = relative.Split(Path.DirectorySeparatorChar);

                if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    || segments.Contains("obj", StringComparer.OrdinalIgnoreCase)
                    || segments.Contains(".vs", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_backup))
            {
                Directory.Delete(_backup, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时快照清不掉不影响本轮回放。
        }
    }
}
