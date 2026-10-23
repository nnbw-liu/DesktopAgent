namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// 比较沙箱 work 副本与 manifest：新增 / 修改 / 删除，并为文本文件生成 diff。
/// 二进制与 &gt;256KB 的文件只给大小，不生成文本差异。
/// </summary>
public static class WorkspaceDiff
{
    public const long TextDiffLimitBytes = 256L * 1024;
    public const int BinaryProbeBytes = 8 * 1024;

    private static readonly IReadOnlySet<string> ExcludedDirectories = new HashSet<string>(
        [".git", ".vs", "bin", "obj", "node_modules", ".tmp", ".idea"],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<SandboxChange>> ComputeAsync(
        string workDirectory,
        string baseDirectory,
        WorkspaceManifest manifest,
        int maxDiffLines = 500,
        CancellationToken cancellationToken = default)
    {
        var manifestByPath = manifest.Entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var workFiles = WorkspaceWalker.EnumerateFiles(workDirectory, ExcludedDirectories)
            .ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);

        var changes = new List<SandboxChange>();

        foreach (var file in workFiles.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!manifestByPath.TryGetValue(file.RelativePath, out var entry))
            {
                changes.Add(await BuildChangeAsync(
                        file.RelativePath,
                        SandboxChangeKind.Added,
                        file.Length,
                        basePath: null,
                        workPath: file.FullPath,
                        maxDiffLines,
                        cancellationToken)
                    .ConfigureAwait(false));
                continue;
            }

            var modified = entry.Length != file.Length;
            if (!modified && entry.Sha256 is not null)
            {
                var hash = await WorkspaceSnapshot.HashFileAsync(file.FullPath, cancellationToken)
                    .ConfigureAwait(false);
                modified = !string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
            }
            else if (!modified)
            {
                modified = entry.LastWriteUtc.Ticks != file.LastWriteUtc.Ticks;
            }

            if (modified)
            {
                changes.Add(await BuildChangeAsync(
                        file.RelativePath,
                        SandboxChangeKind.Modified,
                        file.Length,
                        Path.Combine(baseDirectory, ToPlatformPath(file.RelativePath)),
                        file.FullPath,
                        maxDiffLines,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        foreach (var entry in manifest.Entries)
        {
            if (!workFiles.ContainsKey(entry.Path))
            {
                changes.Add(new SandboxChange(entry.Path, SandboxChangeKind.Deleted, entry.Length, false, null));
            }
        }

        return changes.OrderBy(change => change.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static async Task<SandboxChange> BuildChangeAsync(
        string relativePath,
        SandboxChangeKind kind,
        long size,
        string? basePath,
        string? workPath,
        int maxDiffLines,
        CancellationToken cancellationToken)
    {
        var isBinary = workPath is not null && await IsBinaryAsync(workPath, cancellationToken).ConfigureAwait(false);
        string? diff = null;

        if (!isBinary && workPath is not null && size <= TextDiffLimitBytes)
        {
            var oldText = basePath is not null && File.Exists(basePath)
                ? await File.ReadAllTextAsync(basePath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            var newText = await File.ReadAllTextAsync(workPath, cancellationToken).ConfigureAwait(false);
            diff = TextDiff.Create(relativePath, oldText, newText, maxDiffLines);
        }

        return new SandboxChange(relativePath, kind, size, isBinary, diff);
    }

    private static async Task<bool> IsBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[BinaryProbeBytes];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: BinaryProbeBytes,
            useAsync: true);
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }

    private static string ToPlatformPath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);
}
