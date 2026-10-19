using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SDemo.DesktopAgent.Services;

/// <inheritdoc />
public sealed class AppEnvironmentService : IAppEnvironmentService
{
    public string AppName => "SDemo.DesktopAgent";

    public string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public string RuntimeDescription =>
        $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

    public string OsDescription => RuntimeInformation.OSDescription;

    public string WebView2Version
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return "未安装";
            }
        }
    }
}
