namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 提供应用与运行环境信息。示例：Blazor 组件可通过 DI 直接读取 .NET 侧数据。
/// </summary>
public interface IAppEnvironmentService
{
    string AppName { get; }

    string Version { get; }

    string RuntimeDescription { get; }

    string OsDescription { get; }

    /// <summary>本机 WebView 运行时版本（桌面端是 WebView2，移动端是系统 WebView）。</summary>
    string WebViewVersion { get; }

    /// <summary>当前宿主名称，例如 “Blazor Hybrid · WPF” / “Blazor Hybrid · MAUI (Windows)”。</summary>
    string HostName { get; }

    /// <summary>在系统文件管理器中打开目录（不存在时先创建）；移动端不支持时抛 <see cref="PlatformNotSupportedException"/>。</summary>
    void OpenFolder(string path);
}
