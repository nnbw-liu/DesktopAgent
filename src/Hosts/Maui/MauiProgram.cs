using Microsoft.Extensions.Logging;
using SDemo.DesktopAgent.Services;
#if WINDOWS
using SDemo.DesktopAgent.Platform.Windows.Composition;
#endif

namespace SDemo.DesktopAgent.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Blazor Hybrid 必需：注册 BlazorWebView 相关服务。
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // 平台实现（MAUI / Windows / Android）。
        builder.Services.AddSingleton<IAppPaths, MauiAppPaths>();
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        builder.Services.AddSingleton<ISystemThemeProvider, MauiSystemThemeProvider>();
        builder.Services.AddSingleton<IAppEnvironmentService, MauiEnvironmentService>();
        builder.Services.AddSingleton<IFolderPickerService, MauiFolderPickerService>();
        builder.Services.AddSingleton<AttachmentStore>();

        // 平台无关：设置、主题、模型、会话、技能、Agent（与 WPF 同一套）。
        builder.Services.AddDesktopAgentCore();

        // 沙箱骨架（S0）：Windows 用 AppContainer / Windows Sandbox 运行器，Android 用不可用实现。
#if WINDOWS
        builder.Services.AddWindowsSandbox();
        builder.Services.AddSingleton<IFileLauncherService,
            SDemo.DesktopAgent.Platform.Windows.Services.ShellFileLauncher>();
#else
        builder.Services.AddUnavailableSandbox();
#endif

        var app = builder.Build();

        // 内置技能：把随应用发布的技能包补进技能库；失败不能挡住启动。
        try
        {
            app.Services.GetRequiredService<BundledSkillInstaller>().EnsureInstalled();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"内置技能安装失败：{ex.Message}");
        }

        // S6：启动时清理过期沙箱数据；Android 是空实现，不会做任何 IO。
        _ = app.Services.GetRequiredService<ISandboxMaintenance>().CleanupAsync();

        return app;
    }
}
