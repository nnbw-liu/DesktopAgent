namespace SDemo.DesktopAgent.Services;

/// <summary>选择文件夹。桌面端用原生对话框；移动端没有等价物，返回 null 或退化为选择文件。</summary>
public interface IFolderPickerService
{
    /// <summary>返回选中的目录；取消（或平台不支持）返回 null。</summary>
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);
}
