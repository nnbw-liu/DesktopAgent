using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>插件运行时视图：目录结果 + 用户设置（总开关 / 启用 / 入口 / 顺序 / 上限）。</summary>
public sealed class PluginRegistryTests
{
    private const string ThreeSurfaces = """
        {
          "id": "demo",
          "name": "演示",
          "capabilities": [ { "id": "main", "name": "演示", "skill": "skills/main.md" } ],
          "mounts": [
            { "id": "sidebar", "surface": "sidebar", "capability": "main", "order": 10 },
            { "id": "composer", "surface": "composer", "capability": "main", "order": 10 },
            { "id": "bubble", "surface": "message", "capability": "main", "order": 10 }
          ]
        }
        """;

    [Fact]
    public void 总开关关闭_所有入口都消失()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        Assert.Equal(3, CountMounts(registry));

        settings.Current.Plugins.Enabled = false;
        settings.Save();

        Assert.Empty(registry.Plugins);
        Assert.Null(registry.Find("demo"));
        Assert.Null(registry.FindMount("demo/composer"));
        Assert.Equal(0, CountMounts(registry));
    }

    [Fact]
    public void 停用单个插件_入口与查找都消失但显示名还在()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.GetOrAddEntry("demo").Enabled = false;
        settings.Save();

        Assert.Null(registry.Find("demo"));
        Assert.Null(registry.FindMount("demo/composer"));
        Assert.Equal(0, CountMounts(registry));

        // 历史消息的「翻译 · SDemo」归属仍然要能显示。
        Assert.Equal("演示", registry.DisplayName("demo"));
    }

    [Fact]
    public void 关掉侧栏开关_只影响侧栏()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.GetOrAddEntry("demo").InSidebar = false;
        settings.Save();

        Assert.Empty(registry.Mounts(PluginSurface.Sidebar));
        Assert.Single(registry.Mounts(PluginSurface.Composer));
        Assert.Single(registry.Mounts(PluginSurface.Message));
        Assert.Null(registry.FindMount("demo/sidebar"));
        Assert.NotNull(registry.FindMount("demo/composer"));
    }

    [Fact]
    public void 关掉输入框开关_侧栏跟着不可用_气泡仍可用()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.GetOrAddEntry("demo").InComposer = false;
        settings.Save();

        Assert.Empty(registry.Mounts(PluginSurface.Composer));

        // 侧栏入口只是「新建一个预选该插件的会话」，输入框入口关了以后进去也看不到插件，所以一起隐藏。
        Assert.Empty(registry.Mounts(PluginSurface.Sidebar));
        Assert.Null(registry.FindMount("demo/sidebar"));

        Assert.Single(registry.Mounts(PluginSurface.Message));
    }

    [Fact]
    public void 关掉气泡开关_只影响气泡()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.GetOrAddEntry("demo").InMessage = false;
        settings.Save();

        Assert.Empty(registry.Mounts(PluginSurface.Message));
        Assert.Null(registry.FindMount("demo/bubble"));
        Assert.Single(registry.Mounts(PluginSurface.Sidebar));
        Assert.Single(registry.Mounts(PluginSurface.Composer));
    }

    [Fact]
    public void 侧栏开关不影响输入框与气泡()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.GetOrAddEntry("demo").InSidebar = false;
        settings.Save();

        Assert.Empty(registry.Mounts(PluginSurface.Sidebar));
        Assert.Single(registry.Mounts(PluginSurface.Composer));
        Assert.Single(registry.Mounts(PluginSurface.Message));
    }

    [Fact]
    public void 用户顺序覆盖manifest顺序()
    {
        using var temp = new TempPlugins();
        var alpha = """
            {
              "id": "alpha",
              "name": "Alpha",
              "capabilities": [ { "id": "main", "name": "Alpha", "skill": "skills/main.md" } ],
              "mounts": [ { "id": "composer", "surface": "composer", "capability": "main", "order": 50 } ]
            }
            """;
        var beta = alpha
            .Replace("\"alpha\"", "\"beta\"")
            .Replace("Alpha", "Beta")
            .Replace("\"order\": 50", "\"order\": 10");

        temp.WriteLocal("alpha", alpha, ("skills/main.md", Fixtures.Skill));
        temp.WriteLocal("beta", beta, ("skills/main.md", Fixtures.Skill));

        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        Assert.Equal(new[] { "beta", "alpha" }, registry.Mounts(PluginSurface.Composer).Select(mount => mount.PluginId));

        settings.Current.Plugins.GetOrAddEntry("alpha").Order = 1;
        settings.Save();

        Assert.Equal(new[] { "alpha", "beta" }, registry.Mounts(PluginSurface.Composer).Select(mount => mount.PluginId));
    }

    [Fact]
    public void 设置变化会触发Changed()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        var changes = 0;
        registry.Changed += (_, _) => changes++;

        settings.Current.Plugins.MaxInComposer = 5;
        settings.Save();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void 重新扫描也会刷新视图()
    {
        using var temp = new TempPlugins();
        var settings = new StubSettingsService();
        var catalog = temp.CreateCatalog();
        var registry = new PluginRegistry(catalog, settings);

        Assert.Empty(registry.Plugins);

        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        catalog.Scan();

        Assert.Single(registry.Plugins);
        Assert.Single(registry.Mounts(PluginSurface.Composer));
    }

    [Fact]
    public void 未知插件条目不影响加载()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", ThreeSurfaces, ("skills/main.md", Fixtures.Skill));
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.Entries.Add(new PluginEntryPreferences { Id = "ghost", Enabled = false });
        settings.Save();

        Assert.Single(registry.Mounts(PluginSurface.Composer));
    }

    [Fact]
    public void 显示上限夹到1到20()
    {
        using var temp = new TempPlugins();
        var settings = new StubSettingsService();
        var registry = new PluginRegistry(temp.CreateCatalog(), settings);

        settings.Current.Plugins.MaxInComposer = 0;
        settings.Current.Plugins.MaxInSidebar = 999;

        Assert.Equal(1, registry.MaxFor(PluginSurface.Composer));
        Assert.Equal(20, registry.MaxFor(PluginSurface.Sidebar));
        Assert.Equal(int.MaxValue, registry.MaxFor(PluginSurface.Message));
    }

    private static int CountMounts(PluginRegistry registry) =>
        registry.Mounts(PluginSurface.Sidebar).Count
        + registry.Mounts(PluginSurface.Composer).Count
        + registry.Mounts(PluginSurface.Message).Count;
}
