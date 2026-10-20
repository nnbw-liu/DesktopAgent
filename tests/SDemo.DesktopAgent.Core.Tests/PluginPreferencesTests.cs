using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>插件的设置节点：旧文件兼容、落盘 / 读回、隐私确认列表。</summary>
public sealed class PluginPreferencesTests : IDisposable
{
    private readonly string _root;

    public PluginPreferencesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-plugin-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void 旧设置文件没有plugins节点时用默认值()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{ "language": "zh-CN" }""");

        var service = new SettingsService(new StubAppPaths(_root), new StubSecureStore());

        Assert.NotNull(service.Current.Plugins);
        Assert.True(service.Current.Plugins.Enabled);
        Assert.Equal(3, service.Current.Plugins.MaxInComposer);
        Assert.Equal(6, service.Current.Plugins.MaxInSidebar);
        Assert.True(service.Current.Plugins.RememberParameterValues);
        Assert.Equal(PluginOutputMode.Bilingual, service.Current.Plugins.DefaultOutputMode);
        Assert.Empty(service.Current.Plugins.Entries);
        Assert.Empty(service.Current.Plugins.PrivacyAcknowledged);
    }

    [Fact]
    public void plugins写成null也能启动()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{ "plugins": null }""");

        var service = new SettingsService(new StubAppPaths(_root), new StubSecureStore());

        Assert.NotNull(service.Current.Plugins);
        Assert.True(service.Current.Plugins.Enabled);
    }

    [Fact]
    public void 条目落盘后能读回_并保留没设过的开关()
    {
        var paths = new StubAppPaths(_root);
        var store = new StubSecureStore();

        var first = new SettingsService(paths, store);
        var entry = first.Current.Plugins.GetOrAddEntry("translate");
        entry.InSidebar = true;
        entry.InComposer = false;
        entry.Order = 10;
        first.Current.Plugins.PrivacyAcknowledged.Add("translate");
        first.Save();

        var reloaded = new SettingsService(paths, store);
        var back = Assert.Single(reloaded.Current.Plugins.Entries);

        Assert.Equal("translate", back.Id);
        Assert.True(back.InSidebar);
        Assert.False(back.InComposer);
        Assert.Equal(10, back.Order);
        Assert.Null(back.Enabled);   // 没动过的开关保持 null（继续用 manifest 默认）
        Assert.Contains("translate", reloaded.Current.Plugins.PrivacyAcknowledged);
    }

    [Fact]
    public void 同一个插件只生成一个条目()
    {
        var prefs = new PluginPreferences();

        var first = prefs.GetOrAddEntry("translate");
        var second = prefs.GetOrAddEntry("translate");

        Assert.Same(first, second);
        Assert.Single(prefs.Entries);
        Assert.Same(first, prefs.FindEntry("translate"));
        Assert.Null(prefs.FindEntry("missing"));
        Assert.Null(prefs.FindEntry(null));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(20, 20)]
    [InlineData(99, 20)]
    public void 显示上限夹到1到20(int input, int expected) =>
        Assert.Equal(expected, PluginPreferences.ClampLimit(input));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响测试结果。
        }
    }
}
