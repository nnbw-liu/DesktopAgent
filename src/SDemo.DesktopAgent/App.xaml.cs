using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using SDemo.DesktopAgent.Services;
using SDemo.DesktopAgent.Theme;

namespace SDemo.DesktopAgent;

/// <summary>
/// 应用入口：负责组装 DI 容器，并承载 WPF + Blazor Hybrid 的宿主窗口。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IThemeService? _themeService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsWebView2RuntimeInstalled())
        {
            MessageBox.Show(
                "未检测到 Microsoft Edge WebView2 Runtime，Blazor Hybrid 界面无法启动。\n" +
                "请安装后重试：https://developer.microsoft.com/microsoft-edge/webview2/",
                "缺少 WebView2 运行时",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
            return;
        }

        _serviceProvider = CreateServiceProvider();

        // 先应用主题资源，再显示窗口，避免启动瞬间出现配色不一致。
        _themeService = _serviceProvider.GetRequiredService<IThemeService>();
        ThemeResources.Apply(_themeService.Current);
        _themeService.ThemeChanged += (_, theme) => ThemeResources.Apply(theme);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    /// <summary>系统主题变化（WM_SETTINGCHANGE / ImmersiveColorSet）后重新探测。</summary>
    internal void RefreshTheme() => _themeService?.Refresh();

    /// <summary>
    /// 组装应用级 DI 容器。WPF 与 Blazor 组件共享同一个容器实例。
    /// </summary>
    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        // Blazor Hybrid 必需：注册 BlazorWebView 相关服务。
        services.AddWpfBlazorWebView();

        // 应用服务注册区。
        services.AddSingleton<IAppEnvironmentService, AppEnvironmentService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IModelCatalogService, ModelCatalogService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<FileWriteApprovalService>();
        services.AddSingleton<AgentToolbox>();
        services.AddSingleton<IAgentChatService, AgentChatService>();

        // WPF 窗口。
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static bool IsWebView2RuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }
}
