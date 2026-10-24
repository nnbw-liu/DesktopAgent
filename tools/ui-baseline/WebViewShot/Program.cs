using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SDemo.UiBaseline.WebViewShot;

/// <summary>
/// 用 WebView2 把 <c>docs/reports/ui-baseline/*.html</c> 截成 PNG。
/// 与 Playwright 版（tools/ui-baseline）二选一：系统装了 WebView2 运行时就能离线出图。
/// </summary>
internal static class Program
{
    private static readonly (string Html, string Png)[] Pages =
    [
        ("01-empty.html", "01-empty.png"),
        ("02-chat.html", "02-chat.png"),
        ("03-settings.html", "03-settings.png"),
        ("04-compaction.html", "04-compaction.png")
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        var directory = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine("docs", "reports", "ui-baseline");

        var missing = Pages
            .Select(page => Path.Combine(directory, page.Html))
            .Where(path => !File.Exists(path))
            .ToArray();

        if (missing.Length > 0)
        {
            Console.Error.WriteLine("缺少 HTML 基线，先运行 UiBaselineTests：");
            foreach (var path in missing)
            {
                Console.Error.WriteLine("  " + path);
            }

            return 1;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Width = 1200,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -20000,
            Top = 0
        };

        var webView = new WebView2();
        window.Content = webView;

        var exitCode = 0;

        window.Loaded += async (_, _) =>
        {
            try
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                foreach (var (html, png) in Pages)
                {
                    var source = Path.Combine(directory, html);
                    var target = Path.Combine(directory, png);

                    await NavigateAsync(webView, new Uri(source).AbsoluteUri);

                    // 让 CSS 与字体完成布局，再按内容高度调整窗口。
                    await Task.Delay(400);
                    var height = await EvaluateAsync(webView, "Math.max(900, document.body.scrollHeight)");
                    window.Height = Math.Clamp(height, 900, 4000);
                    await Task.Delay(300);

                    await using var stream = File.Create(target);
                    await webView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png,
                        stream);

                    Console.WriteLine($"{png}  ({window.Height:0}px)");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally
            {
                window.Close();
                app.Shutdown();
            }
        };

        window.Show();
        app.Run();
        return exitCode;
    }

    private static Task NavigateAsync(WebView2 webView, string url)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
            completion.TrySetResult();

        webView.NavigationCompleted += OnCompleted;

        try
        {
            webView.Source = new Uri(url);
        }
        finally
        {
            completion.Task.ContinueWith(_ => webView.NavigationCompleted -= OnCompleted);
        }

        return completion.Task;
    }

    private static async Task<double> EvaluateAsync(WebView2 webView, string script)
    {
        var json = await webView.ExecuteScriptAsync(script);

        return double.TryParse(json, out var value) ? value : 900;
    }
}
