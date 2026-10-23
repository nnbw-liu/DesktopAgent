using System.IO;
using System.Security.Cryptography;

namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// 工作区快照：把工作区复制成“基线”（base）与沙箱可写的副本（work），并生成 manifest。
/// base 只供宿主做 diff / 冲突检测，沙箱不可见。
/// </summary>
public static class WorkspaceSnapshot
{
    /// <summary>小于该值的文件计算 SHA-256；更大的文件只记录长度与修改时间。</summary>
    public const long HashLimitBytes = 4L * 1024 * 1024;

    public static async Task<WorkspaceManifest> CreateAsync(
        string workspaceRoot,
        string baseDirectory,
        string workDirectory,
        WorkspaceCopyOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"工作区不存在：{workspaceRoot}");
        }

        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(workDirectory);

        var entries = new List<WorkspaceManifestEntry>();
        var skipped = new List<string>();
        long totalBytes = 0;

        foreach (var file in WorkspaceWalker.EnumerateFiles(workspaceRoot, options.ExcludedDirectories, skipped))
        {
            cancellationToken.ThrowIfCancellationRequested();

            totalBytes += file.Length;
            if (entries.Count + 1 > options.MaxFiles || totalBytes > options.MaxBytes)
            {
                throw new WorkspaceTooLargeException(
                    totalBytes,
                    options.MaxBytes,
                    entries.Count + 1,
                    options.MaxFiles);
            }

            var basePath = Path.Combine(baseDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var workPath = Path.Combine(workDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(workPath)!);

            File.Copy(file.FullPath, basePath, overwrite: true);
            File.SetLastWriteTimeUtc(basePath, file.LastWriteUtc);
            File.Copy(basePath, workPath, overwrite: true);
            File.SetLastWriteTimeUtc(workPath, file.LastWriteUtc);

            string? hash = null;
            if (file.Length <= HashLimitBytes)
            {
                hash = await HashFileAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            }

            entries.Add(new WorkspaceManifestEntry(file.RelativePath, file.Length, file.LastWriteUtc, hash));
        }

        return new WorkspaceManifest
        {
            Entries = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray(),
            Skipped = skipped.OrderBy(path => path, StringComparer.Ordinal).ToArray()
        };
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
