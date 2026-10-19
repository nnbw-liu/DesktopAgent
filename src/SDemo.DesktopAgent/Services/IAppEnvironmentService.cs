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

    /// <summary>本机 WebView2 运行时版本。</summary>
    string WebView2Version { get; }
}
