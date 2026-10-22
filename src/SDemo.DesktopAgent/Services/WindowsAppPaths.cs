using System.IO;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent;

/// <summary>
/// 桌面端数据目录：%APPDATA%\SDemo\DesktopAgent。
/// MAUI 宿主换成 FileSystem.AppDataDirectory，其余服务不用改。
/// </summary>
public sealed class WindowsAppPaths : IAppPaths
{
    public WindowsAppPaths()
    {
        // 调试 / 双端并存时可以用 SDEMO_DATA_ROOT 指到另一个目录，避免和正式实例抢同一个 SQLite。
        var overrideRoot = Environment.GetEnvironmentVariable("SDEMO_DATA_ROOT");
        var workspaceOverride = Environment.GetEnvironmentVariable("SDEMO_WORKSPACE_ROOT");
        var sandboxOverride = Environment.GetEnvironmentVariable("SDEMO_SANDBOX_ROOT");

        DataRoot = !string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.GetFullPath(overrideRoot)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SDemo",
                "DesktopAgent");

        SettingsFile = Path.Combine(DataRoot, "settings.json");
        ChatsRoot = Path.Combine(DataRoot, "chats");
        UploadsRoot = Path.Combine(DataRoot, "uploads");
        SkillsRoot = Path.Combine(DataRoot, "skills");
        PluginsRoot = Path.Combine(DataRoot, "plugins");
        DefaultWorkspaceRoot = !string.IsNullOrWhiteSpace(workspaceOverride)
            ? Path.GetFullPath(workspaceOverride)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SDemo DesktopAgent");

        // 沙箱放本地应用数据，不跟着漫游配置走：job 目录里有工作区副本，体积大且不应同步。
        SandboxRoot = !string.IsNullOrWhiteSpace(sandboxOverride)
            ? Path.GetFullPath(sandboxOverride)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SDemo",
                "DesktopAgent",
                "sandbox");
    }

    public string DataRoot { get; }

    public string SettingsFile { get; }

    public string ChatsRoot { get; }

    public string UploadsRoot { get; }

    public string SkillsRoot { get; }

    public string PluginsRoot { get; }

    public string DefaultWorkspaceRoot { get; }

    public string SandboxRoot { get; }
}
