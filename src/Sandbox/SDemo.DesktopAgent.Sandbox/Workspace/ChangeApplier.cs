using System.Text.Json;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Sandbox;

/// <summary>应用变更的结果。Conflicts 表示真实工作区在快照后又被改过，绝不能覆盖。</summary>
public sealed record ApplyResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// 把用户选中的变更从 job 的 work 副本写回真实工作区。
/// S2–S5 只处理 Added / Modified；S6 起可处理 Deleted：不永久删除，移动到 job 专属的 trash 目录。
/// </summary>
public sealed class ChangeApplier
{
    /// <summary>兼容旧调用：没有 trash 目录时删除类变更一律跳过。</summary>
    public Task<ApplyResult> ApplyAsync(
        string workspaceRoot,
        string workDirectory,
        WorkspaceManifest manifest,
        IReadOnlyList<SandboxChange> changes,
        IReadOnlySet<string> selected,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(workspaceRoot, workDirectory, manifest, changes, selected, trashDirectory: null, cancellationToken);

    public async Task<ApplyResult> ApplyAsync(
        string workspaceRoot,
        string workDirectory,
        WorkspaceManifest manifest,
        IReadOnlyList<SandboxChange> changes,
        IReadOnlySet<string> selected,
        string? trashDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var manifestByPath = manifest.Entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var changesByPath = changes.ToDictionary(change => change.RelativePath, StringComparer.OrdinalIgnoreCase);

        var selectedPaths = selected
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var applied = new List<string>();
        var skipped = new List<string>();
        var conflicts = new List<string>();
        var pendingWrites = new List<(string RelativePath, string Source, string Target)>();
        var pendingDeletes = new List<(string RelativePath, string Target)>();

        // 先整体预检：只要有一条写冲突，就一个文件都不写，避免半套变更落盘。
        // 删除类冲突只跳过该文件：用户可能只想删另一份没被改过的文件。
        foreach (var relativePath in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!changesByPath.TryGetValue(relativePath, out var change))
            {
                skipped.Add(relativePath);
                continue;
            }

            if (!TryResolveTarget(root, relativePath, out var target, out _))
            {
                skipped.Add(relativePath);
                continue;
            }

            if (HasReparsePoint(root, target))
            {
                skipped.Add(relativePath);
                continue;
            }

            if (change.Kind == SandboxChangeKind.Deleted)
            {
                if (string.IsNullOrWhiteSpace(trashDirectory) || !File.Exists(target))
                {
                    skipped.Add(relativePath);
                    continue;
                }

                if (!manifestByPath.TryGetValue(relativePath, out var entry)
                    || !await IsUnchangedAsync(target, entry, cancellationToken).ConfigureAwait(false))
                {
                    // 删除前必须确认文件仍是快照里的版本：用户改过就跳过，绝不误删。
                    skipped.Add(relativePath);
                    continue;
                }

                pendingDeletes.Add((relativePath, target));
                continue;
            }

            var source = Path.Combine(workDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                skipped.Add(relativePath);
                continue;
            }

            var conflict = await CheckConflictAsync(target, relativePath, change.Kind, manifestByPath, cancellationToken)
                .ConfigureAwait(false);
            if (conflict)
            {
                conflicts.Add(relativePath);
                continue;
            }

            pendingWrites.Add((relativePath, source, target));
        }

        if (conflicts.Count > 0)
        {
            return new ApplyResult([], skipped, conflicts);
        }

        foreach (var item in pendingWrites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                WriteAtomic(item.Source, item.Target);
                applied.Add(item.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(item.RelativePath);
            }
        }

        foreach (var item in pendingDeletes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                MoveToTrash(trashDirectory!, item.RelativePath, item.Target);
                applied.Add(item.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(item.RelativePath);
            }
        }

        MarkApplied(workDirectory, manifest, applied);
        RecordApplied(workDirectory, applied);
        return new ApplyResult(applied, skipped, conflicts);
    }

    /// <summary>
    /// 记录用户已处理（忽略 / 关闭面板）的路径。Janitor 据此判断 job 是否还有“未处理”的变更，
    /// 避免把没确认过的 diff 清掉；被忽略的变更不会写回工作区。
    /// </summary>
    public void MarkResolved(string workDirectory, IEnumerable<string> relativePaths) =>
        RecordApplied(workDirectory, relativePaths.ToArray());

    /// <summary>删除前的一致性校验：与 manifest 记录的长度 / hash / 时间一致才允许移动。</summary>
    private static async Task<bool> IsUnchangedAsync(
        string target,
        WorkspaceManifestEntry entry,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(target);
        if (entry.Sha256 is not null)
        {
            var hash = await WorkspaceSnapshot.HashFileAsync(target, cancellationToken).ConfigureAwait(false);
            return string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        return info.Length == entry.Length && info.LastWriteTimeUtc.Ticks == entry.LastWriteUtc.Ticks;
    }

    /// <summary>移动到 <c>trash/{jobId}/{relativePath}</c>；保留相对路径，便于用户找回。</summary>
    private static void MoveToTrash(string trashDirectory, string relativePath, string target)
    {
        var root = Path.GetFullPath(trashDirectory);
        var destination = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!SandboxPathGuard.IsUnder(root, destination))
        {
            throw new IOException($"trash 路径越界：{relativePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(target, destination, overwrite: true);
    }

    private static void MarkApplied(
        string workDirectory,
        WorkspaceManifest manifest,
        IReadOnlyList<string> applied)
    {
        if (applied.Count == 0)
        {
            return;
        }

        var appliedPaths = applied.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updated = new WorkspaceManifest
        {
            ProtocolVersion = manifest.ProtocolVersion,
            Entries = manifest.Entries
                .Select(entry => appliedPaths.Contains(entry.Path) ? entry with { Applied = true } : entry)
                .ToArray(),
            Skipped = manifest.Skipped
        };

        var jobRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(workDirectory));
        if (!string.IsNullOrEmpty(jobRoot))
        {
            var manifestPath = Path.Combine(jobRoot, SandboxProtocol.ManifestFileName);
            if (File.Exists(manifestPath))
            {
                updated.Save(manifestPath);
            }
        }
    }

    /// <summary>
    /// 记录本 job 已处理过的路径（含 Added / Modified / Deleted，应用或忽略）。
    /// Janitor 靠它判断“job 里还有没有未处理的变更”，避免把用户还没确认的 diff 直接清掉。
    /// </summary>
    private static void RecordApplied(string workDirectory, IReadOnlyList<string> applied)
    {
        if (applied.Count == 0)
        {
            return;
        }

        var jobRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(workDirectory));
        if (string.IsNullOrEmpty(jobRoot))
        {
            return;
        }

        var path = Path.Combine(jobRoot, SandboxProtocol.AppliedChangesFileName);
        var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path), SandboxJson.Options);
                if (existing is not null)
                {
                    appliedPaths.UnionWith(existing);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 标记文件损坏时按“只有本次应用”处理，仍然比丢掉整份记录安全。
        }

        appliedPaths.UnionWith(applied);

        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(appliedPaths.OrderBy(item => item, StringComparer.Ordinal), SandboxJson.Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<bool> CheckConflictAsync(
        string target,
        string relativePath,
        SandboxChangeKind kind,
        IReadOnlyDictionary<string, WorkspaceManifestEntry> manifest,
        CancellationToken cancellationToken)
    {
        var exists = File.Exists(target);

        if (kind == SandboxChangeKind.Added)
        {
            // 快照时没有、现在却存在：别人刚创建了同名文件，不能覆盖。
            return exists;
        }

        if (!manifest.TryGetValue(relativePath, out var entry))
        {
            return true;
        }

        if (!exists)
        {
            return true;
        }

        var info = new FileInfo(target);
        if (entry.Sha256 is not null)
        {
            var currentHash = await WorkspaceSnapshot.HashFileAsync(target, cancellationToken).ConfigureAwait(false);
            return !string.Equals(currentHash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        return info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.LastWriteUtc.Ticks;
    }

    private static void WriteAtomic(string source, string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".sdemo-tmp-{Guid.NewGuid():N}");
        try
        {
            File.Copy(source, temporary, overwrite: true);

            if (File.Exists(target))
            {
                try
                {
                    // Replace 保留目标文件原有的 ACL，比 Move 覆盖更安全。
                    File.Replace(temporary, target, destinationBackupFileName: null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    // 回退到 Move 覆盖。
                }
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool TryResolveTarget(string root, string relativePath, out string target, out string? reason)
    {
        target = string.Empty;
        reason = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            reason = "路径为空。";
            return false;
        }

        var normalizedRelative = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(relativePath)
            || normalizedRelative.StartsWith("//", StringComparison.Ordinal)
            || normalizedRelative.Split('/').Any(segment => segment is ".." or "."))
        {
            reason = "拒绝绝对路径或 . / .. 路径。";
            return false;
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!SandboxPathGuard.IsUnder(root, fullPath)
            || Path.TrimEndingDirectorySeparator(fullPath).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            reason = "路径逃逸。";
            return false;
        }

        target = fullPath;
        return true;
    }

    private static bool HasReparsePoint(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
