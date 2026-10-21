using System.IO;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 把 SandboxHost 的发布文件放到 <c>sandbox\host</c>。首次运行复制一次，之后直接复用。
/// 搜索顺序：<c>SDEMO_SANDBOX_HOST_SOURCE</c> → 应用目录下的 <c>sandbox-host</c> → 应用目录本身。
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
            if (File.Exists(target))
            {
                _installedPath = target;
                return target;
            }

            var sourceDirectory = FindSourceDirectory();
            if (sourceDirectory is null)
            {
                throw new InvalidOperationException(
                    "找不到 SandboxHost 发布目录。请先运行：" +
                    "dotnet publish src/SDemo.DesktopAgent.SandboxHost -c Release -r win-x64 " +
                    "--self-contained true -o artifacts/sandbox-host，并把该目录放到应用目录的 sandbox-host 下。");
            }

            Directory.CreateDirectory(_directory.HostDirectory);
            CopyDirectory(sourceDirectory, _directory.HostDirectory, cancellationToken);

            if (!File.Exists(target))
            {
                throw new InvalidOperationException(
                    $"SandboxHost 复制后仍找不到可执行文件：{target}");
            }

            _installedPath = target;
            return target;
        }
        finally
        {
            _gate.Release();
        }
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
