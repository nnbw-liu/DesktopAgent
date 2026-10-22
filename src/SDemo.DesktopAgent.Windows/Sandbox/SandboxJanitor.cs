using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱清理器（S6 实现）：
/// <list type="bullet">
/// <item>启动时删除超过 24 小时的 job 目录；有未应用变更的 job 跳过并在报告里列出。</item>
/// <item>删除残留 <c>*.tmp</c> / <c>*.wsb</c>（超过 1 小时，避免碰到正在运行的作业）。</item>
/// <item>删除超过 7 天的 trash 条目。</item>
/// <item>“清理沙箱数据”：清空 jobs / trash / 临时文件，并删除 AppContainer profile。</item>
/// </list>
/// 只动沙箱根目录下的数据，不按进程名杀全局进程。
/// </summary>
public sealed class SandboxJanitor : ISandboxMaintenance
{
    public static readonly TimeSpan JobMaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan TrashMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResidualMaxAge = TimeSpan.FromHours(1);

    private const string TmpPattern = "*.tmp";
    private const string WsbPattern = "*.wsb";

    private readonly SandboxDirectory _directory;
    private readonly AppContainerProfileProvider _profiles;
    private readonly Func<bool> _deleteProfile;
    private volatile SandboxMaintenanceReport? _lastReport;

    public SandboxMaintenanceReport? LastReport => _lastReport;

    public SandboxJanitor(SandboxDirectory directory, AppContainerProfileProvider profiles)
        : this(directory, profiles, AppContainerProfile.Delete)
    {
    }

    /// <summary>测试可以替换 profile 删除动作，避免动到本机真实的 AppContainer profile。</summary>
    public SandboxJanitor(
        SandboxDirectory directory,
        AppContainerProfileProvider profiles,
        Func<bool> deleteProfile)
    {
        _directory = directory;
        _profiles = profiles;
        _deleteProfile = deleteProfile;
    }

    public async Task<SandboxMaintenanceReport> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var deletedJobs = 0;
        var deletedTrash = 0;
        var deletedTemp = 0;
        var pending = new List<string>();

        foreach (var jobDirectory in EnumerateJobDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Age(jobDirectory) < JobMaxAge)
            {
                continue;
            }

            if (await HasUnappliedChangesAsync(jobDirectory, cancellationToken).ConfigureAwait(false))
            {
                pending.Add(Path.GetFileName(jobDirectory));
                continue;
            }

            if (TryDeleteDirectory(jobDirectory))
            {
                deletedJobs++;
            }
        }

        deletedTemp = DeleteResidualFiles(ResidualMaxAge);
        deletedTrash = DeleteOldTrash();

        var report = new SandboxMaintenanceReport(
            deletedJobs,
            pending.Count,
            deletedTrash,
            deletedTemp,
            pending.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        _lastReport = report;
        return report;
    }

    /// <summary>清空沙箱作业数据并删除 AppContainer profile；host 发布目录保留，避免下次启动重新复制。</summary>
    public Task<SandboxMaintenanceReport> PurgeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var deletedJobs = 0;
        foreach (var jobDirectory in EnumerateJobDirectories())
        {
            if (TryDeleteDirectory(jobDirectory))
            {
                deletedJobs++;
            }
        }

        var deletedTrash = 0;
        if (Directory.Exists(_directory.TrashRoot))
        {
            foreach (var trashDirectory in EnumerateDirectoriesSafe(_directory.TrashRoot))
            {
                if (TryDeleteDirectory(trashDirectory))
                {
                    deletedTrash++;
                }
            }
        }

        var deletedTemp = DeleteResidualFiles(TimeSpan.Zero);

        var profileDeleted = _deleteProfile();
        _profiles.Reset();

        var report = new SandboxMaintenanceReport(
            deletedJobs,
            0,
            deletedTrash,
            deletedTemp,
            [])
        {
            ProfileDeleted = profileDeleted
        };
        _lastReport = report;
        return Task.FromResult(report);
    }

    private IEnumerable<string> EnumerateJobDirectories()
    {
        if (!Directory.Exists(_directory.JobsDirectory))
        {
            yield break;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_directory.JobsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (SandboxDirectory.IsValidJobId(Path.GetFileName(directory)))
            {
                yield return directory;
            }
        }
    }

    private static async Task<bool> HasUnappliedChangesAsync(
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(jobDirectory, SandboxProtocol.ManifestFileName);
        var workDirectory = Path.Combine(jobDirectory, SandboxDirectory.WorkFolderName);
        var baseDirectory = Path.Combine(jobDirectory, SandboxDirectory.BaseFolderName);

        // 快照不完整（崩溃在复制中途）时无法判断，按“有未应用变更”处理，宁可多留一天。
        if (!File.Exists(manifestPath) || !Directory.Exists(workDirectory) || !Directory.Exists(baseDirectory))
        {
            return true;
        }

        try
        {
            var manifest = WorkspaceManifest.Load(manifestPath);
            var applied = LoadApplied(jobDirectory);
            var changes = await WorkspaceDiff.ComputeAsync(
                    workDirectory,
                    baseDirectory,
                    manifest,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return changes.Any(change => !applied.Contains(change.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or JsonException)
        {
            return true;
        }
    }

    private static HashSet<string> LoadApplied(string jobDirectory)
    {
        var path = Path.Combine(jobDirectory, SandboxProtocol.AppliedChangesFileName);
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var applied = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path), SandboxJson.Options);
            return new HashSet<string>(applied ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>回收站目录超过 7 天就删；刚应用删除的文件用户还有机会找回。</summary>
    private int DeleteOldTrash()
    {
        if (!Directory.Exists(_directory.TrashRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var directory in EnumerateDirectoriesSafe(_directory.TrashRoot))
        {
            if (Age(directory) < TrashMaxAge)
            {
                continue;
            }

            if (TryDeleteDirectory(directory))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>只清沙箱根目录下的残留 tmp / wsb，不碰 host 与 trash。</summary>
    private int DeleteResidualFiles(TimeSpan maxAge)
    {
        var deleted = 0;
        foreach (var pattern in new[] { TmpPattern, WsbPattern })
        {
            foreach (var file in EnumerateFilesSafe(_directory.Root, pattern))
            {
                if (IsUnder(file, _directory.HostDirectory) || IsUnder(file, _directory.TrashRoot))
                {
                    continue;
                }

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age < maxAge)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 正在被占用的文件留到下次启动再清。
                }
            }
        }

        return deleted;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TimeSpan Age(string path)
    {
        try
        {
            return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TimeSpan.MaxValue;
        }
    }

    private static bool IsUnder(string path, string root) =>
        SandboxPathGuard.IsUnder(root, path);
}
