using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>应用设置的读写入口。设置文件位于 %APPDATA%\SDemo\DesktopAgent\settings.json。</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>保存到磁盘后触发（设置项被修改时）。</summary>
    event EventHandler? Changed;

    void Save();
}
