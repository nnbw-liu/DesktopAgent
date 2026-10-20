using SDemo.DesktopAgent.Services;
using System.IO;

namespace SDemo.DesktopAgent.Maui;

/// <summary>
/// MAUI 端数据目录：
/// Windows 上刻意指向和 WPF 端相同的 %APPDATA%\SDemo\DesktopAgent（共用 settings / 会话库 / 附件）；
/// Android 上落在应用私有目录。
/// </summary>
public sealed class MauiAppPaths : IAppPaths
{
    public MauiAppPaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("SDEMO_DATA_ROOT");
        var workspaceOverride = Environment.GetEnvironmentVariable("SDEMO_WORKSPACE_ROOT");

        DataRoot = !string.IsNullOrWhiteSpace(overrideRoot) ? Path.GetFullPath(overrideRoot) : DefaultDataRoot();

        SettingsFile = Path.Combine(DataRoot, "settings.json");
        ChatsRoot = Path.Combine(DataRoot, "chats");
        UploadsRoot = Path.Combine(DataRoot, "uploads");
        SkillsRoot = Path.Combine(DataRoot, "skills");
        PluginsRoot = Path.Combine(DataRoot, "plugins");
        DefaultWorkspaceRoot = !string.IsNullOrWhiteSpace(workspaceOverride)
            ? Path.GetFullPath(workspaceOverride)
            : DefaultWorkspace();
    }

    public string DataRoot { get; }

    public string SettingsFile { get; }

    public string ChatsRoot { get; }

    public string UploadsRoot { get; }

    public string SkillsRoot { get; }

    public string PluginsRoot { get; }

    public string DefaultWorkspaceRoot { get; }

    private static string DefaultDataRoot()
    {
#if WINDOWS
        // 与 WPF 端完全一致，两个 exe 读同一份配置与会话。
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent");
#else
        return Path.Combine(FileSystem.Current.AppDataDirectory, "SDemo", "DesktopAgent");
#endif
    }

    private static string DefaultWorkspace()
    {
#if WINDOWS
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SDemo DesktopAgent");
#else
        // 移动端没有「我的文档」：文件工具只能落在应用私有目录里。
        return Path.Combine(FileSystem.Current.AppDataDirectory, "workspace");
#endif
    }
}
