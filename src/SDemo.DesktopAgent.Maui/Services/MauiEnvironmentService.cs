using SDemo.DesktopAgent.Services;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SDemo.DesktopAgent.Maui;

/// <inheritdoc />
public sealed class MauiEnvironmentService : IAppEnvironmentService
{
    public string AppName => "SDemo.DesktopAgent";

    public string Version
    {
        get
        {
            try
            {
                return AppInfo.Current.VersionString;
            }
            catch (Exception)
            {
                return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
            }
        }
    }

    public string RuntimeDescription =>
        $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

    public string OsDescription => RuntimeInformation.OSDescription;

    public string HostName
    {
        get
        {
#if WINDOWS
            return "Blazor Hybrid · MAUI (Windows)";
#elif ANDROID
            return "Blazor Hybrid · MAUI (Android)";
#else
            return "Blazor Hybrid · MAUI";
#endif
        }
    }

    public string WebViewVersion
    {
        get
        {
#if WINDOWS
            try
            {
                return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception)
            {
                return "未安装";
            }
#elif ANDROID
            try
            {
                return Android.Webkit.WebView.CurrentWebViewPackage?.VersionName ?? "系统 WebView";
            }
            catch (Exception)
            {
                return "系统 WebView";
            }
#else
            return "未知";
#endif
        }
    }
}