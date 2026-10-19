using System.IO;
using Microsoft.Win32;

namespace SDemo.DesktopAgent.Services;

/// <inheritdoc />
public sealed class ThemeService : IThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    private readonly ISettingsService _settings;

    public ThemeService(ISettingsService settings)
    {
        _settings = settings;
        Preference = settings.Current.Theme;
        Current = Resolve();
    }

    public AppTheme Current { get; private set; }

    public ThemePreference Preference { get; private set; }

    public event EventHandler<AppTheme>? ThemeChanged;

    public void SetPreference(ThemePreference preference)
    {
        if (Preference == preference)
        {
            return;
        }

        Preference = preference;
        _settings.Current.Theme = preference;
        _settings.Save();

        Apply(Resolve());
    }

    public void Refresh()
    {
        // 跟随系统时才响应系统主题变化。
        if (Preference != ThemePreference.System)
        {
            return;
        }

        Apply(DetectSystemTheme());
    }

    private void Apply(AppTheme theme)
    {
        if (theme == Current)
        {
            return;
        }

        Current = theme;
        ThemeChanged?.Invoke(this, theme);
    }

    private AppTheme Resolve() => Preference switch
    {
        ThemePreference.Light => AppTheme.Light,
        ThemePreference.Dark => AppTheme.Dark,
        _ => DetectSystemTheme()
    };

    /// <summary>
    /// 读取 Windows「应用模式」设置：0 = 深色，1 = 浅色（读不到时按浅色处理）。
    /// 调试时可用环境变量 SDEMO_FORCED_THEME=light|dark 强制预览某一主题。
    /// </summary>
    private static AppTheme DetectSystemTheme()
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
