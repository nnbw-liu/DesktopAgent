using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SDemo.DesktopAgent.Services;

/// <summary>选择文件夹（WPF 原生对话框，Blazor 侧通过 DI 调用）。</summary>
public sealed class FolderPickerService : IFolderPickerService
{
    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            completion.TrySetResult(null);
            return completion.Task;
        }

        // 关键：不要在 WebView2 的事件回调栈里直接 ShowDialog。
        // 模态对话框会跑嵌套消息循环并重入 WebView2，触发 EmbeddedBrowserWebView 的原生断言（0x80000003）导致闪退；
        // 这里把对话框推迟到消息循环的下一轮再打开。
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
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

                var owner = Application.Current?.MainWindow;
                var accepted = owner is not null && owner.IsVisible
                    ? dialog.ShowDialog(owner)
                    : dialog.ShowDialog();

                completion.TrySetResult(accepted == true ? dialog.FolderName : null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }));

        return completion.Task;
    }
}
