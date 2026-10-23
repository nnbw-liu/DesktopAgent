using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱子进程的环境变量：白名单 + 沙箱内 TEMP / USERPROFILE / LOCALAPPDATA。
/// 不传 API Key / Token / 用户 PATH；LOCALAPPDATA 必须存在——AppContainer 启动路径缺它会返回 203。
/// </summary>
public static class SandboxEnvironment
{
    public static IReadOnlyDictionary<string, string> Build(string homeDirectory)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        }

        var system32 = Path.Combine(windows, "System32");
        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0");
        var temp = Path.Combine(homeDirectory, "temp");
        var localAppData = Path.Combine(homeDirectory, "AppData", "Local");
        var roamingAppData = Path.Combine(homeDirectory, "AppData", "Roaming");

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows) ?? @"C:\",
            ["ComSpec"] = Path.Combine(system32, "cmd.exe"),
            ["PATH"] = string.Join(Path.PathSeparator, system32, windows, Path.Combine(system32, "Wbem"), powershell),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC",
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["USERPROFILE"] = homeDirectory,
            ["HOMEDRIVE"] = Path.GetPathRoot(homeDirectory) ?? @"C:\",
            ["HOMEPATH"] = homeDirectory[(Path.GetPathRoot(homeDirectory)?.Length ?? 0)..],
            ["APPDATA"] = roamingAppData,
            ["LOCALAPPDATA"] = localAppData,
            ["PSModulePath"] = Path.Combine(powershell, "Modules")
        };
    }

    /// <summary>生成 CREATE_UNICODE_ENVIRONMENT 需要的双 NUL 结尾块；调用方用 FreeHGlobal 释放。</summary>
    public static IntPtr CreateBlock(IReadOnlyDictionary<string, string> variables)
    {
        var builder = new StringBuilder();
        foreach (var pair in variables.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');

        var chars = builder.ToString().ToCharArray();
        var buffer = Marshal.AllocHGlobal(chars.Length * sizeof(char));
        Marshal.Copy(chars, 0, buffer, chars.Length);
        return buffer;
    }
}
