using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 当前工作区目录。文件工具的根目录跟着当前聊天室所属的工作区走。
/// </summary>
public sealed class WorkspaceContext
{
    public string RootPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SDemo DesktopAgent");
}
