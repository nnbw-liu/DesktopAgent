using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// AppContainer profile 及其 Package SID。
/// 创建失败（权限被策略禁用 / API 不可用）时抛 <see cref="AppContainerUnavailableException"/>，
/// 调用方必须据此拒绝执行，不得降级到无沙箱。
/// </summary>
public sealed class AppContainerProfile : IDisposable
{
    public const string ProfileName = "SDemo.DesktopAgent.Sandbox";

    private IntPtr _sidPointer;
    private bool _disposed;

    private AppContainerProfile(IntPtr sidPointer, string sidString)
    {
        _sidPointer = sidPointer;
        SidString = sidString;
        Sid = new SecurityIdentifier(sidString);
    }

    public SecurityIdentifier Sid { get; }

    public string SidString { get; }

    /// <summary>传给 <c>SECURITY_CAPABILITIES.AppContainerSid</c> 的原生指针；Dispose 后失效。</summary>
    internal IntPtr SidPointer => _sidPointer;

    /// <summary>
    /// 确保 profile 存在。首次调用会创建 profile；已存在时（HRESULT 0x800700B7）派生 SID。
    /// </summary>
    public static AppContainerProfile Ensure() => Ensure(ProfileName);

    public static AppContainerProfile Ensure(string profileName)
    {
        var hr = NativeMethods.CreateAppContainerProfile(
            profileName,
            "SDemo DesktopAgent Sandbox",
            "Runs untrusted PowerShell / PDF parsing for SDemo DesktopAgent",
            IntPtr.Zero,
            0,
            out var sidPointer);

        if (hr == NativeMethods.ErrorAlreadyExistsHResult)
        {
            hr = NativeMethods.DeriveAppContainerSidFromAppContainerName(profileName, out sidPointer);
        }

        if (hr < 0)
        {
            throw new AppContainerUnavailableException(
                $"创建 AppContainer profile 失败（HRESULT 0x{hr:X8}）：{new Win32Exception(hr & 0xFFFF).Message}");
        }

        try
        {
            if (!NativeMethods.ConvertSidToStringSid(sidPointer, out var stringSidPointer))
            {
                throw new AppContainerUnavailableException(
                    $"无法把 AppContainer SID 转成字符串：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            try
            {
                var sidString = Marshal.PtrToStringUni(stringSidPointer)
                                ?? throw new AppContainerUnavailableException("AppContainer SID 为空。");
                return new AppContainerProfile(sidPointer, sidString);
            }
            finally
            {
                NativeMethods.LocalFree(stringSidPointer);
            }
        }
        catch
        {
            NativeMethods.FreeSid(sidPointer);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sidPointer != IntPtr.Zero)
        {
            NativeMethods.FreeSid(_sidPointer);
            _sidPointer = IntPtr.Zero;
        }
    }
}
