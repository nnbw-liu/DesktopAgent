using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>一次 Windows Sandbox 虚拟机的实例句柄。没有官方生命周期 API，只能按进程终止。</summary>
public interface IWindowsSandboxInstance : IDisposable
{
    /// <summary>启动器进程的 PID（WindowsSandbox.exe 可能很快退出，仅用于诊断）。</summary>
    int? ProcessId { get; }

    /// <summary>启动时刻；终止时只杀这个时刻之后启动的 Windows Sandbox 进程。</summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>终止虚拟机：杀掉本次启动的 Windows Sandbox 进程树。</summary>
    void Terminate();
}

/// <summary>启动 .wsb 的抽象，测试可以注入假实现，不必真的开虚拟机。</summary>
public interface IWindowsSandboxLauncher
{
    /// <summary>启动 <paramref name="configurationPath"/> 指向的 .wsb，返回实例句柄。</summary>
    /// <exception cref="Win32Exception">启动失败。</exception>
    IWindowsSandboxInstance Launch(string configurationPath);
}

/// <summary>
/// 通过 <c>WindowsSandbox.exe &lt;file.wsb&gt;</c> 启动沙箱。
/// Windows Sandbox 没有官方生命周期 API：终止只能按“进程名 + 启动时间”杀进程，
/// 因此这里限定只杀本次启动时刻之后出现的 WindowsSandbox* 进程，并保留 5 秒时钟余量。
/// </summary>
public sealed class WindowsSandboxProcessLauncher : IWindowsSandboxLauncher
{
    public IWindowsSandboxInstance Launch(string configurationPath)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
        var startedAt = DateTimeOffset.Now;

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = Quote(configurationPath),
            UseShellExecute = true
        });

        return new WindowsSandboxInstance(process, startedAt);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed class WindowsSandboxInstance : IWindowsSandboxInstance
    {
        private readonly Process? _process;
        private bool _terminated;

        public WindowsSandboxInstance(Process? process, DateTimeOffset startedAt)
        {
            _process = process;
            StartedAt = startedAt;
        }

        public DateTimeOffset StartedAt { get; }

        public int? ProcessId
        {
            get
            {
                try
                {
                    return _process is { HasExited: false } ? _process.Id : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public void Terminate()
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;
            TerminateProcesses(StartedAt);
        }

        public void Dispose()
        {
            _process?.Dispose();
        }
    }

    /// <summary>Windows Sandbox 相关的进程名（不同 Windows 版本出现的进程略有差异）。</summary>
    private static readonly string[] SandboxProcessNames =
    [
        "WindowsSandbox",
        "WindowsSandboxClient",
        "WindowsSandboxRemoteSession",
        "WindowsSandboxServer"
    ];

    private static void TerminateProcesses(DateTimeOffset startedAt)
    {
        var threshold = startedAt.UtcDateTime.AddSeconds(-5);

        foreach (var name in SandboxProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited || process.StartTime.ToUniversalTime() < threshold)
                        {
                            continue;
                        }

                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5_000);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException
                                                   or Win32Exception
                                                   or NotSupportedException)
                    {
                        // 进程已退出 / 权限不足：跳过，不让清理失败影响结果。
                    }
                }
            }
        }
    }
}
