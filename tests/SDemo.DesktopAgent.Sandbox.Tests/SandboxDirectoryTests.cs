using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class SandboxDirectoryTests
{
    [Theory]
    [InlineData("job-00000001")]
    [InlineData("probe-0123456789abcdef0123456789abcdef")]
    [InlineData("selftest-abc")]
    public void 合法JobId被接受(string jobId) =>
        Assert.True(SandboxDirectory.IsValidJobId(jobId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has space")]
    [InlineData("job_00000001")]
    [InlineData("job/00000001")]
    [InlineData("job.00000001")]
    public void 非法JobId被拒绝(string? jobId) =>
        Assert.False(SandboxDirectory.IsValidJobId(jobId));

    [Fact]
    public void 布局落在沙箱根目录下()
    {
        using var host = new SandboxTestHost();
        var directory = host.SandboxDirectory;
        var layout = directory.GetLayout("job-00000001");

        Assert.Equal(Path.Combine(directory.JobsDirectory, "job-00000001"), layout.Root);
        Assert.Equal(Path.Combine(layout.Root, "input"), layout.InputDirectory);
        Assert.Equal(Path.Combine(layout.Root, "work"), layout.WorkDirectory);
        Assert.Equal(Path.Combine(layout.Root, "home"), layout.HomeDirectory);
        Assert.Equal(Path.Combine(layout.Root, "output"), layout.OutputDirectory);
    }

    [Fact]
    public void 越界路径被识别()
    {
        using var host = new SandboxTestHost();
        var directory = host.SandboxDirectory;

        Assert.True(directory.IsUnderRoot(directory.Root, out _));
        Assert.True(directory.IsUnderRoot(Path.Combine(directory.Root, "jobs", "job-00000001"), out _));
        Assert.False(directory.IsUnderRoot(Path.Combine(directory.Root, "..", "outside"), out _));
        Assert.False(directory.IsUnderRoot(host.Root, out _));
        Assert.False(directory.IsUnderRoot(@"C:\Windows", out _));
    }

    [Fact]
    public void EnsureJob创建目录并给ProfileSid最小权限()
    {
        using var host = new SandboxTestHost();
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);

        var layout = host.SandboxDirectory.EnsureJob("job-00000001", profile!.Sid);
        Assert.True(Directory.Exists(layout.InputDirectory));
        Assert.True(Directory.Exists(layout.WorkDirectory));
        Assert.True(Directory.Exists(layout.HomeDirectory));
        Assert.True(Directory.Exists(layout.OutputDirectory));

        var inputRules = SandboxAcl.GetRules(layout.InputDirectory, profile.Sid);
        Assert.Contains(inputRules, rule =>
            rule.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.ReadAndExecute));

        var workRules = SandboxAcl.GetRules(layout.WorkDirectory, profile.Sid);
        Assert.Contains(workRules, rule =>
            rule.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.Modify));

        var rootRules = SandboxAcl.GetRules(layout.Root, profile.Sid);
        Assert.Contains(rootRules, rule =>
            rule.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.Traverse));
    }
}
