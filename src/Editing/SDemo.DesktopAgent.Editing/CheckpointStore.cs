using System.Security.Cryptography;
using System.Text.Json;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Editing;

/// <summary>checkpoint 里的单个文件快照；<see cref="Existed"/> 为 false 表示当时还没有这个文件。</summary>
public sealed record CheckpointFile(string Path, string? Hash, bool Existed, string? AfterHash = null);

/// <summary>一次 turn 的 checkpoint 清单。</summary>
public sealed record CheckpointManifest(
    string Id,
    string SessionId,
    string TurnId,
    DateTime At,
    List<CheckpointFile> Files);

/// <summary>撤销结果；冲突时默认不覆盖，调用方确认后可 force 再次恢复。</summary>
public sealed record CheckpointRestoreResult(
    bool Success,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Restored)
{
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// 编辑前备份（P5）：同一 turn 的多文件编辑共用一个 checkpoint；保留最近 20 个。
/// 恢复时若文件在 checkpoint 后被外部改动，返回冲突，需用户确认后 force。
/// </summary>
public sealed class CheckpointStore
{
    public const int KeepPerSession = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly IToolAuditLog? _audit;
    private readonly object _sync = new();

    public CheckpointStore(IAppPaths paths, IToolAuditLog? audit = null)
        : this(Path.Combine(paths.DataRoot, "checkpoints"), audit)
    {
    }

    public CheckpointStore(string root, IToolAuditLog? audit = null)
    {
        _root = root;
        _audit = audit;
    }

    public string Root => _root;

    /// <summary>把文件当前内容备份进本 turn 的 checkpoint；返回 checkpoint id。</summary>
    public string Capture(
        string sessionId,
        string turnId,
        string workspaceRoot,
        IEnumerable<string> relativePaths)
    {
        lock (_sync)
        {
            var manifest = FindOrCreate(sessionId, turnId);
            var directory = CheckpointDirectory(manifest);
            Directory.CreateDirectory(Path.Combine(directory, "files"));

            foreach (var relative in relativePaths.Select(WorkspacePath.NormalizeRelative)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (manifest.Files.Any(file => string.Equals(file.Path, relative, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var source = WorkspacePath.Resolve(workspaceRoot, relative, allowRoot: false);
                var existed = File.Exists(source);
                string? hash = existed ? HashFile(source) : null;

                if (existed)
                {
                    var target = Path.Combine(directory, "files", relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                }

                manifest.Files.Add(new CheckpointFile(relative, hash, existed));
            }

            manifest = manifest with { Files = manifest.Files };
            WriteManifest(directory, manifest);
            Cleanup(sessionId);
            return manifest.Id;
        }
    }

    /// <summary>编辑成功后记录「本 turn 改完的 hash」，用于区分模型自己的改动与外部后续改动。</summary>
    public void MarkEdited(string sessionId, string turnId, string workspaceRoot, IEnumerable<string> relativePaths)
    {
        lock (_sync)
        {
            var manifest = LoadSession(sessionId)
                .FirstOrDefault(item => string.Equals(item.TurnId, turnId, StringComparison.Ordinal));

            if (manifest is null)
            {
                return;
            }

            var changed = false;
            foreach (var relative in relativePaths.Select(WorkspacePath.NormalizeRelative))
            {
                var file = manifest.Files.FirstOrDefault(item =>
                    string.Equals(item.Path, relative, StringComparison.OrdinalIgnoreCase));

                if (file is null)
                {
                    continue;
                }

                var target = WorkspacePath.Resolve(workspaceRoot, relative, allowRoot: false);
                var after = File.Exists(target) ? HashFile(target) : null;
                var index = manifest.Files.IndexOf(file);
                manifest.Files[index] = file with { AfterHash = after };
                changed = true;
            }

            if (changed)
            {
                WriteManifest(CheckpointDirectory(manifest), manifest);
            }
        }
    }

    /// <summary>最近一个 checkpoint；没有时返回 null。</summary>
    public CheckpointManifest? Latest(string sessionId)
    {
        lock (_sync)
        {
            return LoadSession(sessionId).OrderByDescending(item => item.At).FirstOrDefault();
        }
    }

    /// <summary>
    /// 取某个文件「这次改动前」的内容：从新到旧找第一个包含该路径的 checkpoint，
    /// 读它备份的快照。用于界面上的 diff 预览，不改磁盘、不动快照。
    /// </summary>
    /// <param name="existed">当时文件是否存在；false 表示这是新建文件（改动前为空）。</param>
    public bool TryReadBeforeContent(
        string sessionId,
        string relativePath,
        out string content,
        out bool existed)
    {
        content = string.Empty;
        existed = false;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = WorkspacePath.NormalizeRelative(relativePath);

        lock (_sync)
        {
            foreach (var manifest in LoadSession(sessionId).OrderByDescending(item => item.At))
            {
                var file = manifest.Files.FirstOrDefault(item =>
                    string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));

                if (file is null)
                {
                    continue;
                }

                existed = file.Existed;

                if (!file.Existed)
                {
                    // 新建文件：改动前是空内容，diff 就是「全部新增」。
                    return true;
                }

                var backup = Path.Combine(
                    CheckpointDirectory(manifest),
                    "files",
                    normalized.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(backup))
                {
                    return false;
                }

                try
                {
                    content = File.ReadAllText(backup);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    public IReadOnlyList<CheckpointManifest> List(string sessionId)
    {
        lock (_sync)
        {
            return LoadSession(sessionId).OrderByDescending(item => item.At).ToArray();
        }
    }

    /// <summary>恢复最近一次 checkpoint；有冲突且未 force 时不做任何写入。</summary>
    public CheckpointRestoreResult RestoreLatest(
        string sessionId,
        string workspaceRoot,
        bool force = false)
    {
        lock (_sync)
        {
            var checkpoint = LoadSession(sessionId).OrderByDescending(item => item.At).FirstOrDefault();
            return checkpoint is null
                ? new CheckpointRestoreResult(false, ["没有可撤销的检查点。"], [])
                : Restore(checkpoint, workspaceRoot, force);
        }
    }

    public CheckpointRestoreResult Restore(
        CheckpointManifest checkpoint,
        string workspaceRoot,
        bool force = false)
    {
        lock (_sync)
        {
            var conflicts = new List<string>();
            var changed = new List<string>();

            foreach (var file in checkpoint.Files)
            {
                var target = WorkspacePath.Resolve(workspaceRoot, file.Path, allowRoot: false);
                var exists = File.Exists(target);
                var current = exists ? HashFile(target) : null;

                if (file.Existed)
                {
                    if (!exists)
                    {
                        conflicts.Add(file.Path);
                    }
                    else if (file.AfterHash is not null
                             && !string.Equals(current, file.AfterHash, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add(file.Path);
                    }
                }
                else if (exists && file.AfterHash is not null
                         && !string.Equals(current, file.AfterHash, StringComparison.OrdinalIgnoreCase))
                {
                    // 新建文件后来被用户改过：默认不删除。
                    conflicts.Add(file.Path);
                }
            }

            if (conflicts.Count > 0 && !force)
            {
                return new CheckpointRestoreResult(false, conflicts, []);
            }

            var directory = CheckpointDirectory(checkpoint);
            var trash = Path.Combine(_root, "trash", checkpoint.SessionId, checkpoint.Id);
            Directory.CreateDirectory(trash);

            foreach (var file in checkpoint.Files)
            {
                var target = WorkspacePath.Resolve(workspaceRoot, file.Path, allowRoot: false);

                if (!file.Existed)
                {
                    if (File.Exists(target))
                    {
                        var trashPath = Path.Combine(trash, file.Path.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
                        File.Move(target, trashPath, overwrite: true);
                    }

                    changed.Add(file.Path);
                    continue;
                }

                var backup = Path.Combine(
                    directory,
                    "files",
                    file.Path.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, overwrite: true);
                    changed.Add(file.Path);
                }
            }

            _audit?.Write(new ToolAuditEntry
            {
                SessionId = checkpoint.SessionId,
                ToolId = "checkpoint.restore",
                Approval = "UserInitiated",
                Mode = "direct",
                WorkingDirectory = ".",
                Error = conflicts.Count > 0 ? "用户确认覆盖冲突。" : null
            });

            return new CheckpointRestoreResult(true, conflicts, changed);
        }
    }

    /// <summary>会话删除时清理；Janitor 也可以调用。</summary>
    public void DeleteSession(string sessionId)
    {
        lock (_sync)
        {
            var directory = Path.Combine(_root, sessionId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private CheckpointManifest FindOrCreate(string sessionId, string turnId)
    {
        var existing = LoadSession(sessionId)
            .FirstOrDefault(item => string.Equals(item.TurnId, turnId, StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing;
        }

        var now = DateTime.Now;
        var id = $"{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
        return new CheckpointManifest(id, sessionId, turnId, now, []);
    }

    private string CheckpointDirectory(CheckpointManifest manifest) =>
        Path.Combine(_root, manifest.SessionId, manifest.Id);

    private void WriteManifest(string directory, CheckpointManifest manifest)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private IReadOnlyList<CheckpointManifest> LoadSession(string sessionId)
    {
        var directory = Path.Combine(_root, sessionId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var manifests = new List<CheckpointManifest>();

        foreach (var file in Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<CheckpointManifest>(File.ReadAllText(file), JsonOptions);
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    manifests.Add(manifest);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 坏清单跳过，不影响其他 checkpoint。
            }
        }

        return manifests;
    }

    private void Cleanup(string sessionId)
    {
        var manifests = LoadSession(sessionId).OrderByDescending(item => item.At).ToList();

        foreach (var old in manifests.Skip(KeepPerSession))
        {
            var directory = CheckpointDirectory(old);
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不阻塞编辑。
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
