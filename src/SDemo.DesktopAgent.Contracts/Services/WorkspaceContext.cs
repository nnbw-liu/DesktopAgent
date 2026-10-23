namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 当前工作区目录。文件工具的根目录跟着当前聊天室所属的工作区走；
/// 不在任何工作区的会话（历史聊天）用 <see cref="DefaultRootPath"/>
/// （桌面端是「我的文档\SDemo DesktopAgent」，移动端是应用私有目录）。
/// </summary>
public sealed class WorkspaceContext
{
    public WorkspaceContext(IAppPaths paths)
    {
        DefaultRootPath = paths.DefaultWorkspaceRoot;
        RootPath = DefaultRootPath;
    }

    /// <summary>没有归属工作区时的默认根目录。</summary>
    public string DefaultRootPath { get; }

    /// <summary>当前生效的根目录。</summary>
    public string RootPath { get; set; }
}
