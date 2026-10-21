using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class SandboxAclTests
{
    [Fact]
    public void 根目录只加穿越权限()
    {
        using var host = new SandboxTestHost();
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);
        host.SandboxDirectory.EnsureRoot(profile!.Sid);

        var rules = SandboxAcl.GetRules(host.Paths.SandboxRoot, profile.Sid);

        var rule = Assert.Single(rules);
        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.Traverse
            | System.Security.AccessControl.FileSystemRights.ReadAttributes,
            rule.FileSystemRights & ~System.Security.AccessControl.FileSystemRights.Synchronize);
        Assert.Equal(System.Security.AccessControl.InheritanceFlags.None, rule.InheritanceFlags);
    }

    [Fact]
    public void 重复授权不会重复添加ACE()
    {
        using var host = new SandboxTestHost();
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);

        host.SandboxDirectory.EnsureRoot(profile!.Sid);
        host.SandboxDirectory.EnsureRoot(profile.Sid);

        Assert.Single(SandboxAcl.GetRules(host.Paths.SandboxRoot, profile.Sid));
        Assert.Single(SandboxAcl.GetRules(host.SandboxDirectory.HostDirectory, profile.Sid));
    }

    [Fact]
    public void Host与Input给读执行而Work给修改()
    {
        using var host = new SandboxTestHost();
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);
        host.SandboxDirectory.EnsureRoot(profile!.Sid);
        var layout = host.SandboxDirectory.EnsureJob("job-00000001", profile.Sid);

        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            SandboxAcl.GetRules(host.SandboxDirectory.HostDirectory, profile.Sid).Single().FileSystemRights & ~System.Security.AccessControl.FileSystemRights.Synchronize);
        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            SandboxAcl.GetRules(layout.InputDirectory, profile.Sid).Single().FileSystemRights & ~System.Security.AccessControl.FileSystemRights.Synchronize);
        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.Modify,
            SandboxAcl.GetRules(layout.WorkDirectory, profile.Sid).Single().FileSystemRights & ~System.Security.AccessControl.FileSystemRights.Synchronize);
    }
}
