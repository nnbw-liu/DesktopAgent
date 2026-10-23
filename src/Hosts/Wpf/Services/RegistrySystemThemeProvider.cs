using System.IO;
using Microsoft.Win32;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent;

/// <summary>
/// 桌面端系统主题：读 Windows「应用模式」注册表项（0 = 深色，1 = 浅色）。
/// 调试时可用环境变量 SDEMO_FORCED_THEME=light|dark 强制预览某一主题。
/// </summary>
public sealed class RegistrySystemThemeProvider : ISystemThemeProvider
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    public AppTheme GetSystemTheme()
    {
#if DEBUG
        var forced = Environment.GetEnvironmentVariable("SDEMO_FORCED_THEME");
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Light;
        }

        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Dark;
        }
#endif
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue(AppsUseLightThemeValueName) is int useLightTheme)
            {
                return useLightTheme == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // 读不到系统设置时退回浅色，保证界面可用。
        }

        return AppTheme.Light;
    }
}
