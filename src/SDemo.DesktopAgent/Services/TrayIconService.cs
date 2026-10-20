using System.Drawing;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 系统托盘：图标常驻；关闭主窗口时收进托盘继续运行，托盘菜单可以恢复窗口或真正退出。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Stream? _iconStream;
    private bool _balloonShown;

    public TrayIconService(MainWindow window)
    {
        _window = window;

        _iconStream = TryOpenIconStream();
        _icon = CreateIcon(_iconStream);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "SDemo DesktopAgent",
            Visible = true,
            ContextMenuStrip = CreateMenu()
        };

        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();

        _window.HiddenToTray += (_, _) => ShowBalloon();
    }

    /// <summary>从左下角托盘恢复主窗口。</summary>
    public void RestoreWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();

        // 从别的窗口切回来时，先置顶再取消置顶，保证确实到前台。
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private WinForms.ContextMenuStrip CreateMenu()
    {
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };

        var show = new WinForms.ToolStripMenuItem("显示主窗口");
        show.Click += (_, _) => RestoreWindow();

        var exit = new WinForms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitApplication();

        menu.Items.Add(show);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exit);

        return menu;
    }

    private void OnMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            RestoreWindow();
        }
    }

    /// <summary>第一次收进托盘时提示一下，之后不再打扰。</summary>
    private void ShowBalloon()
    {
        if (_balloonShown)
        {
            return;
        }

        _balloonShown = true;
        _notifyIcon.BalloonTipTitle = "SDemo DesktopAgent";
        _notifyIcon.BalloonTipText = "程序仍在托盘中运行，点击托盘图标可以恢复窗口。";
        _notifyIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>走 App 的退出流程：允许主窗口真正关闭。</summary>
    private static void ExitApplication()
    {
        if (System.Windows.Application.Current is App app)
        {
            app.RequestExit();
            return;
        }

        System.Windows.Application.Current?.Shutdown();
    }

    private static Stream? TryOpenIconStream()
    {
        try
        {
            // 使用和 exe / 窗口一致的 app.ico（Resource 项，pack URI）。
            return System.Windows.Application.GetResourceStream(
                new Uri("Assets/app.ico", UriKind.Relative))?.Stream;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Icon CreateIcon(Stream? stream)
    {
        if (stream is not null)
        {
            try
            {
                return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
            }
            catch (ArgumentException)
            {
                // 图标里没有合适的尺寸时退回 exe 图标。
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            try
            {
                var extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
            catch (ArgumentException)
            {
                // 路径异常时继续用系统默认图标。
            }
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        // 先隐藏再释放，避免托盘里留一个「幽灵图标」。
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconStream?.Dispose();
    }
}
