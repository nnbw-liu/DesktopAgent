namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 应用的数据目录。桌面端落在 %APPDATA%\SDemo\DesktopAgent，
/// 移动端（Android / iOS）必须换成 <c>FileSystem.AppDataDirectory</c>，
/// 因为 <see cref="Environment.SpecialFolder"/> 在移动端拿不到可用路径。
/// </summary>
public interface IAppPaths
{
    /// <summary>应用数据根目录，settings.json / chats / uploads / skills 都在它下面。</summary>
    string DataRoot { get; }

    /// <summary>settings.json 的完整路径。</summary>
    string SettingsFile { get; }

    /// <summary>会话库目录（history.db 与 workspaces\*.db）。</summary>
    string ChatsRoot { get; }

    /// <summary>上传附件目录。</summary>
    string UploadsRoot { get; }

    /// <summary>本地技能库目录（*.md）。</summary>
    string SkillsRoot { get; }

    /// <summary>
    /// 不属于任何工作区的会话默认使用的文件工具根目录。
    /// 桌面端是「我的文档\SDemo DesktopAgent」，移动端退化为应用私有目录。
    /// </summary>
    string DefaultWorkspaceRoot { get; }
}
