using System.IO;
using System.Security;
using Microsoft.Win32;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 宿主上检测到的 Windows Sandbox 事实。只读检测，不启动任何进程、不需要管理员权限。
/// </summary>
public sealed record WindowsSandboxHostInfo(string? EditionId, string ExecutablePath, bool ExecutableExists);

/// <summary>
/// Windows Sandbox 可用性检测（S4）。
/// 规则：Home 版明确不可用；非 Home 版看 <c>WindowsSandbox.exe</c> 是否存在（功能是否启用）；
/// <see cref="SandboxAvailability.Verified"/> 只有在自检（真的启动一次 VM）通过后才为真。
/// </summary>
public static class WindowsSandboxAvailability
{
    public const string FeatureName = "Containers-DisposableClientVM";

    public const string EnableHint =
        "需要管理员执行 Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All 并重启。";

    private const string EditionRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private static readonly HashSet<string> HomeEditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Core",
        "CoreN",
        "CoreSingleLanguage",
        "CoreCountrySpecific"
    };

    /// <summary>读取当前机器的事实：EditionID 与 WindowsSandbox.exe 是否存在。</summary>
    public static WindowsSandboxHostInfo ReadHostInfo()
    {
        string? edition = null;
        try
        {
            edition = Registry.GetValue(EditionRegistryPath, "EditionID", null) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 读不到版本号时按“非 Home”处理：再靠 exe 存在性判断，避免误报“Home 不支持”。
        }

        var executable = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
        var exists = false;
        try
        {
            exists = File.Exists(executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = false;
        }

        return new WindowsSandboxHostInfo(edition, executable, exists);
    }

    /// <summary>把事实映射成可用性。纯函数，测试可直接构造 <see cref="WindowsSandboxHostInfo"/>。</summary>
    public static SandboxAvailability Detect(WindowsSandboxHostInfo info, bool verified)
    {
        if (info.EditionId is not null && HomeEditions.Contains(info.EditionId))
        {
            return new SandboxAvailability(
                SandboxKind.WindowsSandbox,
                Available: false,
                Verified: false,
                Reason: "Windows Home 不支持 Windows Sandbox。",
                Detail: info.EditionId);
        }

        if (!info.ExecutableExists)
        {
            return new SandboxAvailability(
                SandboxKind.WindowsSandbox,
                Available: false,
                Verified: false,
                Reason: "Windows Sandbox 功能未启用。",
                Detail: $"{EnableHint}（未找到 {info.ExecutablePath}）");
        }

        return new SandboxAvailability(
            SandboxKind.WindowsSandbox,
            Available: true,
            Verified: verified,
            Reason: verified ? null : "尚未运行 Windows Sandbox 自检。",
            Detail: verified ? null : "启动一次自检（约 10–30 秒）后才能启用 PowerShell。");
    }
}
