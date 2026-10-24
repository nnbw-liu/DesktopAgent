namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 在宿主系统里打开 / 定位文件。Blazor 组件跑在 WebView 里，不能直接起进程，
/// 因此「打开文件」「在资源管理器里选中」这类动作必须走宿主实现。
/// </summary>
public interface IFileLauncherService
{
    /// <summary>用系统默认程序打开文件；文件不存在或平台不支持时返回 false。</summary>
    Task<bool> OpenFileAsync(string path);

    /// <summary>在系统文件管理器里选中该文件；不支持时退化打开所在目录。</summary>
    Task<bool> RevealFileAsync(string path);
}
