using System.IO;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 把 SandboxHost 的发布文件放到 <c>sandbox\host</c>。首次运行复制一次，之后按 payload 判断是否复用。
/// 搜索顺序：<c>SDEMO_SANDBOX_HOST_SOURCE</c> → 应用目录下的 <c>sandbox-host</c> → 应用目录本身。
/// 只比较“目标可执行文件在不在”不够：S5 给 host 增加了 PdfPig 依赖，旧安装必须被新 payload 覆盖，
/// 否则 PDF 解析会因为缺 DLL 直接失败。
/// </summary>
public sealed class SandboxHostInstaller
{
    public const string ExecutableName = "SDemo.DesktopAgent.SandboxHost.exe";
    public const string SourceEnvironmentVariable = "SDEMO_SANDBOX_HOST_SOURCE";

    private readonly SandboxDirectory _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _installedPath;

    public SandboxHostInstaller(SandboxDirectory directory) => _directory = directory;

    public async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (_installedPath is not null && File.Exists(_installedPath))
        {
            return _installedPath;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = Path.Combine(_directory.HostDirectory, ExecutableName);
            var sourceDirectory = FindSourceDirectory();

            if (sourceDirectory is not null
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory.HostDirectory)),
                    StringComparison.OrdinalIgnoreCase)
                && !IsPayloadCurrent(sourceDirectory, _directory.HostDirectory))
            {
                Directory.CreateDirectory(_directory.HostDirectory);
                CopyDirectory(sourceDirectory, _directory.HostDirectory, cancellationToken);
            }

            if (!File.Exists(target))
            {
                throw new InvalidOperationException(
                    "找不到 SandboxHost 发布目录。请先运行：" +
                    "dotnet publish src/SDemo.DesktopAgent.SandboxHost -c Release -r win-x64 " +
                    "--self-contained true -o artifacts/sandbox-host，并把该目录放到应用目录的 sandbox-host 下。");
            }

            _installedPath = target;
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 目标目录是否与发布目录一致：文件名、大小、最后写入时间都要一致。
    /// <c>File.Copy</c> 会保留源文件的写入时间，所以同一份发布复制过去后必然匹配；
    /// 升级到新 payload（例如新增 DLL）时至少有一个文件不匹配，触发覆盖复制。
    /// 该方法公开是为了让升级逻辑能被测试直接覆盖。
    /// </summary>
    public static bool IsPayloadCurrent(string sourceDirectory, string targetDirectory)
    {
        if (!File.Exists(Path.Combine(targetDirectory, ExecutableName)))
        {
            return false;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relative);

            if (!File.Exists(targetFile))
            {
                return false;
            }

            var sourceInfo = new FileInfo(sourceFile);
            var targetInfo = new FileInfo(targetFile);

            if (sourceInfo.Length != targetInfo.Length)
            {
                return false;
            }

            if (sourceInfo.LastWriteTimeUtc != targetInfo.LastWriteTimeUtc)
            {
                return false;
            }
        }

        return true;
    }

    private string? FindSourceDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(SourceEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            if (File.Exists(fullPath))
            {
                return Path.GetDirectoryName(fullPath);
            }

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "sandbox-host"),
                     AppContext.BaseDirectory
                 })
        {
            if (File.Exists(Path.Combine(candidate, ExecutableName)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
