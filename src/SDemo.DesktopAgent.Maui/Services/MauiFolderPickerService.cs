using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Maui;

/// <summary>
/// 选择工作区目录。
/// Windows：WinRT FolderPicker；Android：系统没有「选文件夹」的原生 UI，返回 null，
/// 工作区退化为应用私有目录（Agent 的文件工具仍可用）。
/// </summary>
public sealed class MauiFolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
#if WINDOWS
        try
        {
            var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (window is null)
            {
                return null;
            }

            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception)
        {
            return null;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}
