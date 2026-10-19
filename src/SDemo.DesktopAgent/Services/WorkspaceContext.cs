using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 当前工作区目录。文件工具的根目录跟着当前聊天室所属的工作区走；
/// 不在任何工作区的会话（历史聊天）用默认文档目录。
/// </summary>
public sealed class WorkspaceContext
{
    public static string DefaultRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SDemo DesktopAgent");

    public string RootPath { get; set; } = DefaultRootPath;
}
