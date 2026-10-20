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
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;

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
        _mainWindow = mainWindow;
        MainWindow = mainWindow;

        // 先建托盘再显示窗口：关闭按钮收进托盘时图标已经在。
        _tray = new TrayIconService(mainWindow);

        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    /// <summary>托盘菜单的「退出」：允许主窗口关闭并结束消息循环。</summary>
    internal void RequestExit()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.ExitRequested = true;
        }

        Shutdown();
    }

    /// <summary>注销 / 关机时不拦截窗口关闭。</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.ExitRequested = true;
        }

        base.OnSessionEnding(e);
    }

    /// <summary>系统主题变化（WM_SETTINGCHANGE / ImmersiveColorSet）后重新探测。</summary>
    internal void RefreshTheme() => _themeService?.Refresh();

    /// <summary>
    /// 组装应用级 DI 容器。WPF 与 Blazor 组件共享同一个容器实例。
    /// 平台无关的服务来自 Core，这里只补 WPF 侧实现。
    /// </summary>
    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        // Blazor Hybrid 必需：注册 BlazorWebView 相关服务。
        services.AddWpfBlazorWebView();

        // 平台实现（WPF / Windows）。
        services.AddSingleton<IAppPaths, WindowsAppPaths>();
        services.AddSingleton<ISecureStore, DpapiSecureStore>();
        services.AddSingleton<ISystemThemeProvider, RegistrySystemThemeProvider>();
        services.AddSingleton<IAppEnvironmentService, WebView2EnvironmentService>();
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddSingleton<AttachmentStore>();

        // 平台无关：设置、主题、模型、会话、技能、Agent。
        services.AddDesktopAgentCore();

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
