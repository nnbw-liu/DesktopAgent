namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 系统外观（浅色 / 深色）来源。桌面端读注册表，移动端读系统 UiMode / RequestedTheme。
/// </summary>
public interface ISystemThemeProvider
{
    /// <summary>当前系统主题；读不到时返回浅色，保证界面可用。</summary>
    AppTheme GetSystemTheme();
}
