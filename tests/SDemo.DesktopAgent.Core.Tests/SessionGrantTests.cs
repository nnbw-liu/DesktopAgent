using SDemo.DesktopAgent.Execution;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>会话级授权：按前缀、按会话、高风险拒绝、可撤销。</summary>
public sealed class SessionGrantTests
{
    [Fact]
    public void 批准后同前缀命中()
    {
        var store = new SessionGrantStore();

        Assert.True(store.Grant("s1", "dotnet build demo.sln", "dotnet", highRisk: false));
        Assert.True(store.IsGranted("s1", "dotnet build demo.sln"));
        Assert.False(store.IsGranted("s1", "dotnet test demo.sln"));
    }

    [Fact]
    public void 会话之间隔离()
    {
        var store = new SessionGrantStore();
        store.Grant("s1", "dotnet build", "dotnet", highRisk: false);

        Assert.False(store.IsGranted("s2", "dotnet build"));
    }

    [Fact]
    public void 高风险命令不写入()
    {
        var store = new SessionGrantStore();

        Assert.False(store.Grant("s1", "git reset --hard", "git", highRisk: true));
        Assert.False(store.IsGranted("s1", "git reset --hard"));
    }

    [Fact]
    public void 撤销与清空()
    {
        var store = new SessionGrantStore();
        store.Grant("s1", "dotnet build", "dotnet", highRisk: false);
        store.Grant("s1", "dotnet test", "dotnet", highRisk: false);

        Assert.True(store.Revoke("s1", "dotnet build"));
        Assert.False(store.IsGranted("s1", "dotnet build"));
        Assert.Single(store.List("s1"));

        store.Clear("s1");
        Assert.Empty(store.List("s1"));
    }
}
