using System.IO;
using Microsoft.Win32;

namespace SDemo.DesktopAgent.Services;

/// <summary>选择文件夹（WPF 原生对话框，Blazor 侧通过 DI 调用）。</summary>
public interface IFolderPickerService
{
    /// <summary>返回选中的目录；取消返回 null。</summary>
    string? PickFolder(string title, string? initialDirectory = null);
}

/// <inheritdoc />
public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
