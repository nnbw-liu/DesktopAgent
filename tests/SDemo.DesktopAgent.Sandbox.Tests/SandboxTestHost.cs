using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Composition;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S1 集成测试宿主：临时沙箱根目录 + 真实 Windows 沙箱服务。
/// 每个测试一个实例，避免 ACL / job 目录互相干扰。
/// </summary>
internal sealed class SandboxTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public SandboxTestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "sdemo-sandbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new TestAppPaths(Root);

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(Paths);
        services.AddSingleton<ISettingsService>(new TestSettingsService());
        services.AddWindowsSandbox();
        _provider = services.BuildServiceProvider();
    }

    public string Root { get; }

    public TestAppPaths Paths { get; }

    public ServiceProvider Provider => _provider;

    public ISandboxJobService Jobs => _provider.GetRequiredService<ISandboxJobService>();

    public SandboxDirectory SandboxDirectory => _provider.GetRequiredService<SandboxDirectory>();

    public AppContainerProfileProvider Profiles => _provider.GetRequiredService<AppContainerProfileProvider>();

    public AppContainerSandboxRunner Runner => _provider.GetRequiredService<AppContainerSandboxRunner>();

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}

internal sealed class TestAppPaths : IAppPaths
{
    public TestAppPaths(string root)
    {
        DataRoot = root;
        SettingsFile = Path.Combine(root, "settings.json");
        ChatsRoot = Path.Combine(root, "chats");
        UploadsRoot = Path.Combine(root, "uploads");
        SkillsRoot = Path.Combine(root, "skills");
        PluginsRoot = Path.Combine(root, "plugins");
        DefaultWorkspaceRoot = Path.Combine(root, "workspace");
        SandboxRoot = Path.Combine(root, "sandbox");
    }

    public string DataRoot { get; }

    public string SettingsFile { get; }

    public string ChatsRoot { get; }

    public string UploadsRoot { get; }

    public string SkillsRoot { get; }

    public string PluginsRoot { get; }

    public string DefaultWorkspaceRoot { get; }

    public string SandboxRoot { get; }
}

internal sealed class TestSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public void Save() => Changed?.Invoke(this, EventArgs.Empty);
}
