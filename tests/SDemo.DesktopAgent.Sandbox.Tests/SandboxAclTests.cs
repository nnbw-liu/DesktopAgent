using System.Security.AccessControl;
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

        var rule = ExplicitRule(host.Paths.SandboxRoot, profile.Sid);
        Assert.Equal(
            FileSystemRights.Traverse | FileSystemRights.ReadAttributes,
            rule.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
    }

    [Fact]
    public void 重复授权不会重复添加ACE()
    {
        using var host = new SandboxTestHost();
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);

        host.SandboxDirectory.EnsureRoot(profile!.Sid);
        host.SandboxDirectory.EnsureRoot(profile.Sid);

        Assert.Single(ExplicitRules(host.Paths.SandboxRoot, profile.Sid));
        Assert.Single(ExplicitRules(host.SandboxDirectory.HostDirectory, profile.Sid));
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
            FileSystemRights.ReadAndExecute,
            ExplicitRule(host.SandboxDirectory.HostDirectory, profile.Sid).FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(
            FileSystemRights.ReadAndExecute,
            ExplicitRule(layout.InputDirectory, profile.Sid).FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(
            FileSystemRights.Modify,
            ExplicitRule(layout.WorkDirectory, profile.Sid).FileSystemRights & ~FileSystemRights.Synchronize);
    }

    [Fact]
    public void 已继承只读权限时仍会补上修改权限()
    {
        using var host = new SandboxTestHost();
        var profile = host.Profiles.TryGet(out var reason);
        Assert.True(profile is not null, reason);

        var parent = Path.Combine(host.Root, "acl-parent");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(parent);
        SandboxAcl.GrantReadExecute(parent, profile!.Sid);
        Directory.CreateDirectory(child);

        // child 已继承 RX；GrantModify 必须新增显式 Modify，而不是被只读 ACE 骗过。
        SandboxAcl.GrantModify(child, profile.Sid);

        Assert.Equal(
            FileSystemRights.Modify,
            ExplicitRule(child, profile.Sid).FileSystemRights & ~FileSystemRights.Synchronize);
    }

    private static FileSystemAccessRule ExplicitRule(string path, System.Security.Principal.SecurityIdentifier sid) =>
        Assert.Single(ExplicitRules(path, sid));

    private static FileSystemAccessRule[] ExplicitRules(string path, System.Security.Principal.SecurityIdentifier sid) =>
        SandboxAcl.GetRules(path, sid).Where(rule => !rule.IsInherited).ToArray();
}
