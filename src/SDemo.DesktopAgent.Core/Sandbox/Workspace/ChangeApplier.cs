using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Sandbox;

/// <summary>应用变更的结果。Conflicts 表示真实工作区在快照后又被改过，绝不能覆盖。</summary>
public sealed record ApplyResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// 把用户选中的变更从 job 的 work 副本写回真实工作区。
/// 只做 Added / Modified；Deleted 在 S2 只展示、不自动删除。
/// </summary>
public sealed class ChangeApplier
{
    public async Task<ApplyResult> ApplyAsync(
        string workspaceRoot,
        string workDirectory,
        WorkspaceManifest manifest,
        IReadOnlyList<SandboxChange> changes,
        IReadOnlySet<string> selected,
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
        var pending = new List<(string RelativePath, string Source, string Target)>();

        // 先整体预检：只要有一条冲突，就一个文件都不写，避免半套变更落盘。
        foreach (var relativePath in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!changesByPath.TryGetValue(relativePath, out var change))
            {
                skipped.Add(relativePath);
                continue;
            }

            if (change.Kind == SandboxChangeKind.Deleted)
            {
                skipped.Add(relativePath);
                continue;
            }

            if (!TryResolveTarget(root, relativePath, out var target, out var reason))
            {
                skipped.Add(relativePath);
                continue;
            }

            if (HasReparsePoint(root, target))
            {
                skipped.Add(relativePath);
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

            pending.Add((relativePath, source, target));
        }

        if (conflicts.Count > 0)
        {
            return new ApplyResult([], skipped, conflicts);
        }

        foreach (var item in pending)
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

        MarkApplied(workDirectory, manifest, applied);
        return new ApplyResult(applied, skipped, conflicts);
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
