using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <inheritdoc />
public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;
    private readonly ISystemThemeProvider _systemTheme;

    public ThemeService(ISettingsService settings, ISystemThemeProvider systemTheme)
    {
        _settings = settings;
        _systemTheme = systemTheme;
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

        Apply(Resolve());
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
        _ => _systemTheme.GetSystemTheme()
    };
}
