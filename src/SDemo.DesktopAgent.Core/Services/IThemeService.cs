namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 系统主题来源。WPF 与 Blazor 两侧都从这里读取，保证界面配色一致。
/// </summary>
public interface IThemeService
{
    /// <summary>当前生效的主题。</summary>
    AppTheme Current { get; }

    /// <summary>用户选择的外观偏好。</summary>
    ThemePreference Preference { get; }

    /// <summary>主题变化时触发（已在 UI 线程上）。</summary>
    event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>修改外观偏好并立即生效，同时写入设置。</summary>
    void SetPreference(ThemePreference preference);

    /// <summary>重新读取系统主题，变化时触发 <see cref="ThemeChanged"/>。</summary>
    void Refresh();
}
