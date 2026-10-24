using System.Diagnostics;
using System.IO;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Services;

/// <summary>
/// Windows 宿主的「打开文件 / 定位文件」实现：只负责把请求交给 Shell，
/// 不读文件内容、不改文件，失败返回 false（界面给一句提示，不抛给用户）。
/// </summary>
public sealed class ShellFileLauncher : IFileLauncherService
{
    public Task<bool> OpenFileAsync(string path) => Task.Run(() =>
    {
        var full = ResolveExisting(path);

        return full is not null && Start(new ProcessStartInfo(full) { UseShellExecute = true });
    });

    public Task<bool> RevealFileAsync(string path) => Task.Run(() =>
    {
        var full = ResolveExisting(path);

        if (full is null)
        {
            return false;
        }

        // /select 会打开资源管理器并选中文件；个别环境不允许参数写作时退化为打开目录。
        return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
        {
            UseShellExecute = true
        }) || Start(new ProcessStartInfo("explorer.exe", $"/e,\"{Path.GetDirectoryName(full)}\"")
        {
            UseShellExecute = true
        });
    });

    /// <summary>相对路径按当前工作目录补全；文件不存在就不启动（避免 Explorer 弹错误框）。</summary>
    private static string? ResolveExisting(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
