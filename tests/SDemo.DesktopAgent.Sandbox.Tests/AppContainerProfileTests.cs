using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>真机集成测试：AppContainer profile 在普通用户下也能创建（不需要管理员）。</summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class AppContainerProfileTests
{
    [Fact]
    public void 创建AppContainerProfile并得到PackageSid()
    {
        using var profile = AppContainerProfile.Ensure();

        Assert.StartsWith("S-1-15-2-", profile.SidString);
        Assert.Equal(profile.SidString, profile.Sid.Value);
    }

    [Fact]
    public void 重复创建返回同一个Sid()
    {
        using var first = AppContainerProfile.Ensure();
        using var second = AppContainerProfile.Ensure();

        Assert.Equal(first.SidString, second.SidString);
    }

    [Fact]
    public void Provider缓存结果且失败不抛异常()
    {
        using var host = new SandboxTestHost();
        var provider = host.Profiles;

        var profile = provider.TryGet(out var reason);

        Assert.True(profile is not null, reason);
        Assert.Null(reason);
        Assert.Same(profile, provider.TryGet(out _));
    }
}
