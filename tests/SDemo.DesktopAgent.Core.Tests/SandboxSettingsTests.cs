using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>沙箱设置：默认值必须有“Required + AppContainer”，旧设置文件也要能加载。</summary>
public sealed class SandboxSettingsTests
{
    [Fact]
    public void 默认要求AppContainer沙箱()
    {
        var settings = new SandboxSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(SandboxRequirement.Required, settings.Requirement);
        Assert.Equal(SandboxKind.AppContainer, settings.Preferred);
        Assert.Equal(30, settings.TimeoutSeconds);
        Assert.Equal(512, settings.MemoryLimitMB);
        Assert.Equal(1024, settings.JobMemoryLimitMB);
        Assert.Equal(8, settings.MaxProcesses);
        Assert.False(settings.WorkspaceReadOnly);
        Assert.Equal(200, settings.WorkspaceCopyLimitMB);
        Assert.False(settings.RetainScriptsInAuditLog);
        Assert.Equal(7, settings.AuditRetentionDays);
    }

    [Fact]
    public void 旧设置文件没有沙箱节点时使用默认值()
    {
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        File.WriteAllText(paths.SettingsFile, """{ "theme": "Dark", "providers": [] }""");

        var service = new SettingsService(paths, new StubSecureStore());

        Assert.NotNull(service.Current.Sandbox);
        Assert.Equal(SandboxRequirement.Required, service.Current.Sandbox.Requirement);
        Assert.Equal(SandboxKind.AppContainer, service.Current.Sandbox.Preferred);
    }

    [Fact]
    public void 保存设置后文件里出现沙箱节点()
    {
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var service = new SettingsService(paths, new StubSecureStore());

        service.Save();

        var json = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"sandbox\"", json);
        Assert.Contains("\"requirement\": \"Required\"", json);
        Assert.Contains("\"preferred\": \"AppContainer\"", json);
        Assert.Contains("\"workspaceCopyLimitMB\": 200", json);
    }

    [Fact]
    public void 沙箱根目录默认在数据目录下()
    {
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);

        Assert.Equal("sandbox", Path.GetFileName(paths.SandboxRoot));
        Assert.StartsWith(paths.DataRoot, paths.SandboxRoot, StringComparison.OrdinalIgnoreCase);
    }
}
