using SDemo.DesktopAgent.Services;
using AppTheme = SDemo.DesktopAgent.Services.AppTheme;
using MauiTheme = Microsoft.Maui.ApplicationModel.AppTheme;

namespace SDemo.DesktopAgent.Maui;

/// <summary>MAUI 端系统主题：Windows 跟随系统设置，Android 跟随深色模式。</summary>
public sealed class MauiSystemThemeProvider : ISystemThemeProvider
{
    public AppTheme GetSystemTheme()
        => Application.Current?.RequestedTheme == MauiTheme.Dark ? AppTheme.Dark : AppTheme.Light;
}
