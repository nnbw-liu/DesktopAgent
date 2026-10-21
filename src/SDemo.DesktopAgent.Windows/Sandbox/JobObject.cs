using System.ComponentModel;
using System.Runtime.InteropServices;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 一次执行的 Job Object：进程数、单进程 / 整 Job 内存、超时 / 取消后的整棵树终止，
/// 以及 KILL_ON_JOB_CLOSE（宿主崩溃时子进程不残留）。
/// </summary>
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    public JobObject(SandboxLimits limits)
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 失败。");
        }

        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
                             | NativeMethods.JobObjectLimitActiveProcess
                             | NativeMethods.JobObjectLimitProcessMemory
                             | NativeMethods.JobObjectLimitJobMemory,
                ActiveProcessLimit = (uint)Math.Max(1, limits.MaxProcesses),
                MinimumWorkingSetSize = UIntPtr.Zero,
                MaximumWorkingSetSize = UIntPtr.Zero
            },
            ProcessMemoryLimit = (UIntPtr)(ulong)Math.Max(1, limits.MemoryLimitMB) * 1024 * 1024,
            JobMemoryLimit = (UIntPtr)(ulong)Math.Max(1, limits.JobMemoryLimitMB) * 1024 * 1024
        };

        if (!NativeMethods.SetExtendedLimitInformation(
                _handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref extended,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "设置 Job Object 资源限制失败。");
        }

        var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass = NativeMethods.JobObjectUiLimitHandles
                                  | NativeMethods.JobObjectUiLimitReadClipboard
                                  | NativeMethods.JobObjectUiLimitWriteClipboard
                                  | NativeMethods.JobObjectUiLimitSystemParameters
                                  | NativeMethods.JobObjectUiLimitDisplaySettings
                                  | NativeMethods.JobObjectUiLimitGlobalAtoms
                                  | NativeMethods.JobObjectUiLimitDesktop
                                  | NativeMethods.JobObjectUiLimitExitWindows
        };

        if (NativeMethods.SetBasicUiRestrictions(
                _handle,
                NativeMethods.JobObjectBasicUiRestrictions,
                ref ui,
                Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()))
        {
            UiRestrictionsApplied = true;
        }
        else
        {
            // UI 限制是纵深防御；申请失败（个别系统策略）不能阻止基本隔离，但要在自检里能看到。
            UiRestrictionsApplied = false;
            UiRestrictionsReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
    }

    /// <summary>UI 限制是否成功应用。</summary>
    public bool UiRestrictionsApplied { get; }

    /// <summary>UI 限制失败原因（成功时为 null）。</summary>
    public string? UiRestrictionsReason { get; }

    public void Assign(IntPtr processHandle)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "把进程加入 Job Object 失败。");
        }
    }

    /// <summary>终止 Job 内全部进程（超时 / 取消时整棵树一起收）。</summary>
    public void Terminate()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.TerminateJobObject(_handle, 1);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
