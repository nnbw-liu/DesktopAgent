using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S6 清理器：过期 job（>24h）、未应用变更保护、残留 tmp / wsb、过期 trash 与彻底清理。
/// profile 删除用注入的假实现，避免测试动到本机真实的 AppContainer。
/// </summary>
public sealed class SandboxJanitorTests
{
    [Fact]
    public async Task 超过24小时的作业被清理而新作业保留()
    {
        using var temp = new TempDirectory();
        var directory = new SandboxDirectory(Path.Combine(temp.Root, "sandbox"));
        directory.EnsureRoot();
        var oldJob = CreateJob(directory, "job-aaaaaaaa", withChange: false, ageDays: 2);
        var newJob = CreateJob(directory, "job-bbbbbbbb", withChange: false, ageDays: 0);

        var janitor = CreateJanitor(directory);
        var report = await janitor.CleanupAsync();

        Assert.Equal(1, report.DeletedJobs);
        Assert.False(Directory.Exists(oldJob));
        Assert.True(Directory.Exists(newJob));
    }

    [Fact]
    public async Task 有未应用变更的作业被跳过()
    {
        using var temp = new TempDirectory();
        var directory = new SandboxDirectory(Path.Combine(temp.Root, "sandbox"));
        directory.EnsureRoot();
        var job = CreateJob(directory, "job-cccccccc", withChange: true, ageDays: 2);

        var janitor = CreateJanitor(directory);
        var report = await janitor.CleanupAsync();

        Assert.Equal(0, report.DeletedJobs);
        Assert.Equal(1, report.PendingJobs);
        Assert.Equal(["job-cccccccc"], report.PendingJobIds);
        Assert.True(Directory.Exists(job));
    }

    [Fact]
    public async Task 用户已应用变更的作业可以清理()
    {
        using var temp = new TempDirectory();
        var directory = new SandboxDirectory(Path.Combine(temp.Root, "sandbox"));
        directory.EnsureRoot();
        var job = CreateJob(directory, "job-dddddddd", withChange: true, ageDays: 2);
        File.WriteAllText(
            Path.Combine(job, SandboxProtocol.AppliedChangesFileName),
            JsonSerializer.Serialize(new[] { "a.txt" }, SandboxJson.Options));
        Directory.SetLastWriteTimeUtc(job, DateTime.UtcNow.AddDays(-2));

        var janitor = CreateJanitor(directory);
        var report = await janitor.CleanupAsync();

        Assert.Equal(1, report.DeletedJobs);
        Assert.Equal(0, report.PendingJobs);
        Assert.False(Directory.Exists(job));
    }

    [Fact]
    public async Task 清理残留tmp与wsb但跳过host和trash()
    {
        using var temp = new TempDirectory();
        var directory = new SandboxDirectory(Path.Combine(temp.Root, "sandbox"));
        directory.EnsureRoot();
        Directory.CreateDirectory(directory.TrashRoot);

        var stale = WriteFile(Path.Combine(directory.Root, "stale.tmp"), "x", DateTime.UtcNow.AddHours(-2));
        var staleWsb = WriteFile(Path.Combine(directory.Root, "stale.wsb"), "x", DateTime.UtcNow.AddHours(-2));
        var fresh = WriteFile(Path.Combine(directory.Root, "fresh.tmp"), "x", DateTime.UtcNow);
        var hostKeep = WriteFile(Path.Combine(directory.HostDirectory, "keep.tmp"), "x", DateTime.UtcNow.AddHours(-2));
        var trashKeep = WriteFile(Path.Combine(directory.TrashRoot, "keep.tmp"), "x", DateTime.UtcNow.AddHours(-2));

        var janitor = CreateJanitor(directory);
        var report = await janitor.CleanupAsync();

        Assert.Equal(2, report.DeletedTempFiles);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleWsb));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(hostKeep));
        Assert.True(File.Exists(trashKeep));
    }

    [Fact]
    public async Task 回收站超过7天被清理()
    {
        using var temp = new TempDirectory();
        var directory = new SandboxDirectory(Path.Combine(temp.Root, "sandbox"));
        directory.EnsureRoot();
        var oldTrash = Path.Combine(directory.TrashRoot, "job-eeeeeeee");
        var newTrash = Path.Combine(directory.TrashRoot, "job-ffffffff");
        Directory.CreateDirectory(oldTrash);
        Directory.CreateDirectory(newTrash);
        Directory.SetLastWriteTimeUtc(oldTrash, DateTime.UtcNow.AddDays(-8));

        var janitor = CreateJanitor(directory);
        var report = await janitor.CleanupAsync();

        Assert.Equal(1, report.DeletedTrashEntries);
        Assert.False(Directory.Exists(oldTrash));
        Assert.True(Directory.Exists(newTrash));
    }

    [Fact]
    public async Task 彻底清理删除作业与回收站并删除profile()
    {
        using var temp = new TempDirectory();
        var directory = new SandboxDirectory(Path.Combine(temp.Root, "sandbox"));
        directory.EnsureRoot();
        var job = CreateJob(directory, "job-11111111", withChange: false, ageDays: 0);
        var trash = Path.Combine(directory.TrashRoot, "job-22222222");
        Directory.CreateDirectory(trash);

        var profileDeleted = false;
        var janitor = new SandboxJanitor(directory, new AppContainerProfileProvider(), () =>
        {
            profileDeleted = true;
            return true;
        });

        var report = await janitor.PurgeAsync();

        Assert.Equal(1, report.DeletedJobs);
        Assert.Equal(1, report.DeletedTrashEntries);
        Assert.True(report.ProfileDeleted);
        Assert.False(Directory.Exists(job));
        Assert.False(Directory.Exists(trash));
        Assert.True(profileDeleted);
    }

    private static SandboxJanitor CreateJanitor(SandboxDirectory directory) =>
        new(directory, new AppContainerProfileProvider(), () => true);

    private static string CreateJob(
        SandboxDirectory directory,
        string jobId,
        bool withChange,
        double ageDays)
    {
        var job = Path.Combine(directory.JobsDirectory, jobId);
        Directory.CreateDirectory(Path.Combine(job, SandboxDirectory.BaseFolderName));
        Directory.CreateDirectory(Path.Combine(job, SandboxDirectory.WorkFolderName));

        IReadOnlyList<WorkspaceManifestEntry> entries = withChange
            ? [new WorkspaceManifestEntry("a.txt", 4, DateTime.UtcNow, null)]
            : [];

        new WorkspaceManifest { Entries = entries }
            .Save(Path.Combine(job, SandboxProtocol.ManifestFileName));

        if (withChange)
        {
            File.WriteAllText(Path.Combine(job, SandboxDirectory.BaseFolderName, "a.txt"), "old\n");
        }

        Directory.SetLastWriteTimeUtc(job, DateTime.UtcNow.AddDays(-ageDays));
        return job;
    }

    private static string WriteFile(string path, string content, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
