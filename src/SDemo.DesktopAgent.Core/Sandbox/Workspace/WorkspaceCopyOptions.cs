namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// 工作区快照的复制策略。默认跳过版本库 / 构建产物 / 本地临时目录，
/// 不跟随符号链接；超过大小或文件数直接报错，绝不静默截断。
/// </summary>
public sealed class WorkspaceCopyOptions
{
    public IReadOnlySet<string> ExcludedDirectories { get; init; } = new HashSet<string>(
        [".git", ".vs", "bin", "obj", "node_modules", ".tmp", ".idea"],
        StringComparer.OrdinalIgnoreCase);

    public long MaxBytes { get; init; } = 200L * 1024 * 1024;

    public int MaxFiles { get; init; } = 20_000;

    /// <summary>保留字段。S2 始终拒绝跟随 reparse point，避免把工作区外内容带进沙箱。</summary>
    public bool FollowSymlinks { get; init; }
}

/// <summary>工作区超过复制上限。错误信息直接展示给用户，建议缩小工作区或提高设置。</summary>
public sealed class WorkspaceTooLargeException : Exception
{
    public WorkspaceTooLargeException(long scannedBytes, long maxBytes, int scannedFiles, int maxFiles)
        : base(BuildMessage(scannedBytes, maxBytes, scannedFiles, maxFiles))
    {
        ScannedBytes = scannedBytes;
        MaxBytes = maxBytes;
        ScannedFiles = scannedFiles;
        MaxFiles = maxFiles;
    }

    public long ScannedBytes { get; }

    public long MaxBytes { get; }

    public int ScannedFiles { get; }

    public int MaxFiles { get; }

    private static string BuildMessage(long scannedBytes, long maxBytes, int scannedFiles, int maxFiles)
    {
        if (scannedFiles > maxFiles)
        {
            return $"工作区文件过多（已扫描 {scannedFiles} 个，上限 {maxFiles} 个）。"
                   + "请缩小工作区，或在设置中提高上限；不要直接运行。";
        }

        return $"工作区过大（已扫描 {ToMegabytes(scannedBytes)} MB，上限 {ToMegabytes(maxBytes)} MB）。"
               + "请缩小工作区，或在设置中提高上限；不要直接运行。";
    }

    private static long ToMegabytes(long bytes) => (bytes + 1024 * 1024 - 1) / (1024 * 1024);
}
