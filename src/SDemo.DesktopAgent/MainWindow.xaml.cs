using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent;

/// <summary>
/// WPF 宿主窗口：自绘标题栏 + BlazorWebView + 原生状态栏。
/// </summary>
public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSettingChange = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly IThemeService _themeService;

    private WebView2CompositionControl? _webView;

    public MainWindow(IServiceProvider serviceProvider, IThemeService themeService)
    {
        InitializeComponent();

        _themeService = themeService;

        // 让 Blazor 组件复用 WPF 侧构建的同一个 DI 容器。
        BlazorView.Services = serviceProvider;

        // WebView2 的运行时数据放到用户目录，避免安装目录只读时启动失败。
        BlazorView.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
        BlazorView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;

        RuntimeText.Text = $".NET {Environment.Version}";

        StateChanged += (_, _) => UpdateChromeState();
        UpdateChromeState();

        _themeService.ThemeChanged += (_, theme) => UpdateWebViewBackground(theme);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmGetMinMaxInfo:
                ApplyWorkAreaToMaximizedBounds(hwnd, lParam);
                handled = true;
                break;

            case WmSettingChange when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == ImmersiveColorSet:
                // 系统切换浅色/深色：WPF 侧替换资源字典，Blazor 侧由 AppShell 订阅后同步。
                (Application.Current as App)?.RefreshTheme();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 无边框窗口最大化时系统会按「工作区 + 边框」计算尺寸，导致内容被裁掉几个像素；
    /// 这里直接把最大化区域钉到当前显示器的工作区。
    /// </summary>
    private static void ApplyWorkAreaToMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.Monitor;

        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;

        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: true);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateChromeState()
    {
        var isMaximized = WindowState == WindowState.Maximized;

        MaximizeButton.Content = isMaximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = isMaximized ? "还原" : "最大化";
    }

    private void OnBlazorWebViewInitializing(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs args)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SDemo",
                "DesktopAgent",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);
            args.UserDataFolder = userDataFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法创建目录时使用 WebView2 默认位置。
        }
    }

    private void OnBlazorWebViewInitialized(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs args)
    {
        _webView = args.WebView;
        UpdateWebViewBackground(_themeService.Current);
    }

    /// <summary>WebView2 在页面加载完成前会铺一层底色，这里让它与当前主题一致，避免闪白。</summary>
    private void UpdateWebViewBackground(AppTheme theme)
    {
        if (_webView is null)
        {
            return;
        }

        _webView.DefaultBackgroundColor = theme == AppTheme.Dark
            ? System.Drawing.Color.FromArgb(0x0B, 0x0D, 0x12)
            : System.Drawing.Color.FromArgb(0xF6, 0xF7, 0xF9);
    }

    // 与 Win32 结构一一对应，未使用的字段由系统填充，这里保留顺序。
#pragma warning disable CS0649
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
#pragma warning restore CS0649

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
