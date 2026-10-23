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
        // 内置插件随程序集发布，不再有输出目录下的 plugins\；
        // 本地目录为空时，仍能扫描到两个内置插件。
        Assert.Null(catalog.BuiltinRoot);
        Assert.Equal(2, catalog.BuiltinPackageCount);
        Assert.Equal(Path.Combine(temp.Root, "plugins"), paths.PluginsRoot);
        Assert.False(catalog.Result.IsEmpty);
        Assert.Equal(["summarize", "translate"], catalog.Result.Usable.Select(plugin => plugin.Id).Order().ToArray());
    }

    [Fact]
    public void 工具链依赖可构造()
    {
        using var temp = new TempPlugins();
        var paths = new StubAppPaths(temp.Root);

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ISecureStore>(new StubSecureStore());
        services.AddSingleton<ISandboxJobService>(new StubSandboxJobService());
        services.AddDesktopAgentCore();

        using var provider = services.BuildServiceProvider();

        // AgentChatService 需要审批 / 审计 / 沙箱作业三个接缝；缺一个都会在启动后崩。
        Assert.NotNull(provider.GetRequiredService<IAgentChatService>());
        Assert.NotNull(provider.GetRequiredService<IToolApprovalService>());
        Assert.NotNull(provider.GetRequiredService<IToolAuditLog>());

        var registry = provider.GetRequiredService<AgentToolRegistry>();
        Assert.Equal(["todo", "search", "files", "edits", "code_edit"], registry.Descriptors.Select(descriptor => descriptor.Id));

        // 文件读写是内置能力：不再依赖输入框 chip；搜索仍由用户逐轮选择。
        Assert.True(registry.Find("files")!.BuiltIn);
        Assert.True(registry.Find("edits")!.BuiltIn);
        Assert.False(registry.Find("search")!.BuiltIn);

        // 校验 PowerShell 之外的核心包都有工具；PowerShell 由 Windows 宿主单独注册。
        Assert.All(registry.Descriptors, descriptor => Assert.NotEmpty(descriptor.Functions));
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
