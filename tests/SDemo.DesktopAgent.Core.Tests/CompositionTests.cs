using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>DI 注册与默认目录（两个宿主都靠这条接缝拿 PluginCatalog）。</summary>
public sealed class CompositionTests
{
    [Fact]
    public void 依赖注入能构造PluginCatalog()
    {
        using var temp = new TempPlugins();
        var paths = new StubAppPaths(temp.Root);

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(paths);
        services.AddDesktopAgentCore();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<PluginCatalog>();

        // UI 的 SandboxChangesPanel 直接注入 ChangeApplier；核心容器必须能构造它。
        Assert.NotNull(provider.GetRequiredService<ChangeApplier>());

        Assert.Equal(Path.Combine(temp.Root, "plugins"), catalog.LocalRoot);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), catalog.BuiltinRoot);
        Assert.Equal(Path.Combine(temp.Root, "plugins"), paths.PluginsRoot);
        Assert.True(catalog.Result.IsEmpty);
        Assert.Empty(catalog.Result.Usable);
    }

    [Fact]
    public void 本地插件目录默认在数据根目录下()
    {
        using var temp = new TempPlugins();
        var paths = new StubAppPaths(temp.Root);

        Assert.Equal("plugins", Path.GetFileName(paths.PluginsRoot));
        Assert.StartsWith(paths.DataRoot, paths.PluginsRoot, StringComparison.OrdinalIgnoreCase);
    }
}
