using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// AppContainer 运行器：CreateProcess(CREATE_SUSPENDED) → 加入 Job Object → ResumeThread。
/// 子进程只有 Profile SID，没有网络 / 凭据 capability；输出走 output 目录文件，避免管道死锁。
/// </summary>
public sealed class AppContainerSandboxRunner : ISandboxRunner
{
    private readonly SandboxDirectory _directory;
    private readonly AppContainerProfileProvider _profiles;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private SandboxProbe? _probe;

    public AppContainerSandboxRunner(
        SandboxDirectory directory,
        AppContainerProfileProvider profiles,
        IAppPaths paths,
        ISettingsService settings)
    {
        _directory = directory;
        _profiles = profiles;
        _paths = paths;
        _settings = settings;
    }

    public SandboxKind Kind => SandboxKind.AppContainer;

    /// <summary>探针（缓存可用性结果）。设置页自检与选择器共用同一个实例。</summary>
    public SandboxProbe Probe => _probe ??= new SandboxProbe(this, _directory, _profiles, _paths, _settings);

    public Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Probe.ProbeAsync(cancellationToken);

    public Task<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!SandboxDirectory.IsValidJobId(request.JobId))
        {
            return Task.FromResult(Failure(
                request.JobId,
                SandboxFailureCode.ProtocolError,
                "jobId 必须是 8–64 个字母 / 数字 / 连字符。",
                stopwatch.Elapsed));
        }

        var profile = _profiles.TryGet(out var profileReason);
        if (profile is null)
        {
            return Task.FromResult(Failure(
                request.JobId,
                SandboxFailureCode.NotAvailable,
                profileReason ?? "AppContainer profile 不可用。",
                stopwatch.Elapsed));
        }

        if (!_directory.IsUnderRoot(request.WorkingDirectory, out var workingDirectory)
            || !_directory.IsUnderRoot(request.OutputDirectory, out var outputDirectory))
        {
            return Task.FromResult(Failure(
                request.JobId,
                SandboxFailureCode.PathEscape,
                "工作目录或输出目录不在沙箱根目录内。",
                stopwatch.Elapsed));
        }

        var layout = _directory.GetLayout(request.JobId);
        if (!workingDirectory.Equals(layout.WorkDirectory, StringComparison.OrdinalIgnoreCase)
            || !outputDirectory.Equals(layout.OutputDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Failure(
                request.JobId,
                SandboxFailureCode.PathEscape,
                "工作目录 / 输出目录与 job 约定不一致。",
                stopwatch.Elapsed));
        }

        if (!File.Exists(request.Executable))
        {
            return Task.FromResult(Failure(
                request.JobId,
                SandboxFailureCode.LaunchFailed,
                $"可执行文件不存在：{request.Executable}",
                stopwatch.Elapsed));
        }

        try
        {
            _directory.EnsureRoot(profile.Sid);
            layout = _directory.EnsureJob(request.JobId, profile.Sid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return Task.FromResult(Failure(
                request.JobId,
                SandboxFailureCode.LaunchFailed,
                $"准备沙箱目录失败：{ex.Message}",
                stopwatch.Elapsed));
        }

        var result = Execute(request, layout, profile, stopwatch, cancellationToken);
        return Task.FromResult(result);
    }

    private SandboxResult Execute(
        SandboxRequest request,
        SandboxJobLayout layout,
        AppContainerProfile profile,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var limits = request.Limits;
        var stdoutPath = Path.Combine(layout.OutputDirectory, "stdout.txt");
        var stderrPath = Path.Combine(layout.OutputDirectory, "stderr.txt");

        JobObject? job = null;
        var stdoutHandle = IntPtr.Zero;
        var stderrHandle = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var securityCapabilities = IntPtr.Zero;
        var processHandle = IntPtr.Zero;
        var threadHandle = IntPtr.Zero;

        try
        {
            job = new JobObject(limits);

            stdoutHandle = OpenInheritableFile(stdoutPath);
            stderrHandle = OpenInheritableFile(stderrPath);

            var environment = SandboxEnvironment.Build(layout.HomeDirectory);
            environmentBlock = SandboxEnvironment.CreateBlock(environment);

            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = (int)NativeMethods.StartfUseStdHandles,
                    hStdOutput = stdoutHandle,
                    hStdError = stderrHandle,
                    hStdInput = IntPtr.Zero
                }
            };

            var attributeSize = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            if (attributeSize == IntPtr.Zero)
            {
                return Failure(
                    request.JobId,
                    SandboxFailureCode.LaunchFailed,
                    $"查询进程属性大小失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    stopwatch.Elapsed);
            }

            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
            {
                return Failure(
                    request.JobId,
                    SandboxFailureCode.LaunchFailed,
                    $"初始化进程属性失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    stopwatch.Elapsed);
            }

            securityCapabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
            Marshal.StructureToPtr(
                new SECURITY_CAPABILITIES
                {
                    AppContainerSid = profile.SidPointer,
                    Capabilities = IntPtr.Zero,
                    CapabilityCount = 0
                },
                securityCapabilities,
                false);

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)NativeMethods.ProcThreadAttributeSecurityCapabilities,
                    securityCapabilities,
                    (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                return Failure(
                    request.JobId,
                    SandboxFailureCode.LaunchFailed,
                    $"设置 AppContainer capability 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    stopwatch.Elapsed);
            }

            startup.lpAttributeList = attributeList;

            var creationFlags = NativeMethods.ExtendedStartupInfoPresent
                                | NativeMethods.CreateSuspended
                                | NativeMethods.CreateUnicodeEnvironment
                                | NativeMethods.CreateNoWindow;

            var commandLine = CommandLineBuilder.Build(request.Executable, request.Arguments);
            var created = NativeMethods.CreateProcess(
                request.Executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                creationFlags,
                environmentBlock,
                layout.WorkDirectory,
                ref startup,
                out var processInformation);

            if (!created)
            {
                return Failure(
                    request.JobId,
                    SandboxFailureCode.LaunchFailed,
                    $"CreateProcess 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    stopwatch.Elapsed);
            }

            processHandle = processInformation.hProcess;
            threadHandle = processInformation.hThread;

            job.Assign(processHandle);

            if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue)
            {
                job.Terminate();
                return Failure(
                    request.JobId,
                    SandboxFailureCode.LaunchFailed,
                    $"ResumeThread 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    stopwatch.Elapsed);
            }

            var timedOut = false;
            var canceled = false;
            var waitFailed = false;
            var timeout = TimeSpan.FromSeconds(Math.Clamp(limits.TimeoutSeconds, 1, 600));

            while (true)
            {
                var wait = NativeMethods.WaitForSingleObject(processHandle, 100);
                if (wait == NativeMethods.WaitObject0)
                {
                    break;
                }

                if (wait == NativeMethods.WaitFailed)
                {
                    waitFailed = true;
                    job.Terminate();
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    job.Terminate();
                    NativeMethods.WaitForSingleObject(processHandle, 5_000);
                    break;
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    timedOut = true;
                    job.Terminate();
                    NativeMethods.WaitForSingleObject(processHandle, 5_000);
                    break;
                }
            }

            NativeMethods.GetExitCodeProcess(processHandle, out var exitCode);

            CloseQuietly(ref stdoutHandle);
            CloseQuietly(ref stderrHandle);

            var stdout = ReadLimitedText(stdoutPath, limits.MaxOutputBytes, out var stdoutTruncated);
            var stderr = ReadLimitedText(stderrPath, limits.MaxOutputBytes, out var stderrTruncated);

            if (waitFailed)
            {
                return Failure(
                    request.JobId,
                    SandboxFailureCode.LaunchFailed,
                    "等待进程退出失败。",
                    stopwatch.Elapsed);
            }

            var failureCode = timedOut
                ? SandboxFailureCode.Timeout
                : canceled
                    ? SandboxFailureCode.Canceled
                    : SandboxFailureCode.None;

            return new SandboxResult
            {
                JobId = request.JobId,
                Kind = Kind,
                ExitCode = unchecked((int)exitCode),
                StdOut = stdout,
                StdErr = stderr,
                TimedOut = timedOut,
                Canceled = canceled,
                Failure = failureCode,
                Artifacts = ["stdout.txt", "stderr.txt"],
                Duration = stopwatch.Elapsed,
                Message = timedOut
                    ? "已终止进程树；以下是截止终止前的输出。"
                    : canceled
                        ? "已取消；进程树已终止。"
                        : stdoutTruncated || stderrTruncated
                            ? "…（输出已截断）"
                            : job.UiRestrictionsApplied
                                ? null
                                : $"Job UI 限制未生效：{job.UiRestrictionsReason}"
            };
        }
        catch (Win32Exception ex)
        {
            return Failure(request.JobId, SandboxFailureCode.LaunchFailed, ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            CloseQuietly(ref processHandle);
            CloseQuietly(ref threadHandle);
            CloseQuietly(ref stdoutHandle);
            CloseQuietly(ref stderrHandle);

            if (securityCapabilities != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(securityCapabilities);
            }

            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }

            job?.Dispose();
        }
    }

    private static IntPtr OpenInheritableFile(string path)
    {
        var attributes = new SECURITY_ATTRIBUTES { bInheritHandle = true };
        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            ref attributes,
            NativeMethods.OpenAlways,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"创建输出文件失败：{path}");
        }

        return handle;
    }

    private static string ReadLimitedText(string path, int maxBytes, out bool truncated)
    {
        truncated = false;
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(stream.Length, maxBytes);
            var buffer = new byte[length];
            stream.ReadExactly(buffer);

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(buffer);
            if (stream.Length > maxBytes)
            {
                truncated = true;
                text += $"{Environment.NewLine}…（输出已截断，上限 {maxBytes} 字节）";
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void CloseQuietly(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    private static SandboxResult Failure(
        string jobId,
        SandboxFailureCode failure,
        string message,
        TimeSpan duration) =>
        new()
        {
            JobId = jobId,
            Kind = SandboxKind.AppContainer,
            ExitCode = -1,
            Failure = failure,
            Message = message,
            Duration = duration
        };
}
