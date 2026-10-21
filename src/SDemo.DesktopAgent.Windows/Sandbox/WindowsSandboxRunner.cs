using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// Windows Sandbox 运行器（S4）：内核级强隔离，仅 Pro / Enterprise / Education 且已启用
/// <c>Containers-DisposableClientVM</c> 时可用。Home 明确不可用，不尝试“凑合”。
///
/// 执行模型：宿主把 input / work / output 三个目录映射进虚拟机，<c>Networking=Disable</c>；
/// 虚拟机登录后执行 input 里的 run.ps1，结果写回映射的 output 目录，宿主轮询 result.json。
/// 没有官方生命周期 API：超时 / 取消 / 完成都按“进程名 + 启动时间”终止虚拟机，这一点在文档中标注。
/// 单实例：同一时间只允许一台沙箱，第二个请求最多排队 <see cref="BusyWaitSeconds"/> 秒。
/// </summary>
public sealed class WindowsSandboxRunner : ISandboxRunner
{
    public const int BusyWaitSeconds = 60;

    public const int PollIntervalMilliseconds = 200;

    private const int MaximumTimeoutSeconds = 600;

    private readonly IWindowsSandboxLauncher _launcher;
    private readonly Func<WindowsSandboxHostInfo> _hostInfo;
    private readonly SandboxDirectory? _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Windows Sandbox 没有官方生命周期 API：应用退出时至少把本次会话里仍在跑的虚拟机杀掉，
    // 避免留下无主的 WindowsSandbox* 进程（硬杀进程 / 断电仍会残留，S6 的 Janitor 兜底）。
    private static readonly object ActiveGate = new();
    private static readonly List<IWindowsSandboxInstance> ActiveInstances = [];

    private volatile bool _verified;

    static WindowsSandboxRunner()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TerminateActiveInstances();
    }

    /// <param name="directory">
    /// 沙箱目录布局。提供时强制 job 根目录必须等于该布局下的 <c>jobs\{jobId}</c>，
    /// 防止调用方把任意目录（例如真实工作区）当 job 根映射进可写的 work / output。
    /// </param>
    public WindowsSandboxRunner(
        IWindowsSandboxLauncher launcher,
        Func<WindowsSandboxHostInfo> hostInfo,
        SandboxDirectory? directory = null)
    {
        _launcher = launcher;
        _hostInfo = hostInfo;
        _directory = directory;
    }

    public SandboxKind Kind => SandboxKind.WindowsSandbox;

    /// <summary>自检通过后置为 true；失败会清回 false（重新自检失败不应继续显示“已验证”）。</summary>
    public void MarkVerified(bool verified) => _verified = verified;

    public SandboxAvailability Detect() => WindowsSandboxAvailability.Detect(_hostInfo(), _verified);

    /// <summary>只做事实检测，不启动 VM；Verified 只有在自检跑通后才为真。</summary>
    public Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Detect());

    public async Task<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!SandboxDirectory.IsValidJobId(request.JobId))
        {
            return Failure(request.JobId, SandboxFailureCode.ProtocolError,
                "jobId 必须是 8–64 个字母 / 数字 / 连字符。", stopwatch.Elapsed);
        }

        var spec = request.WindowsSandbox;
        if (spec is null)
        {
            return Failure(request.JobId, SandboxFailureCode.ProtocolError,
                "Windows Sandbox 请求缺少 job 描述（WindowsSandbox）。", stopwatch.Elapsed);
        }

        var specError = ValidateSpec(request.JobId, spec);
        if (specError is not null)
        {
            return Failure(request.JobId, SandboxFailureCode.PathEscape, specError, stopwatch.Elapsed);
        }

        var availability = Detect();
        if (!availability.Available)
        {
            return Failure(request.JobId, SandboxFailureCode.NotAvailable,
                availability.Reason ?? "Windows Sandbox 不可用。", stopwatch.Elapsed);
        }

        bool acquired;
        try
        {
            acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(BusyWaitSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Canceled(request.JobId, stopwatch.Elapsed);
        }

        if (!acquired)
        {
            return Failure(request.JobId, SandboxFailureCode.LaunchFailed,
                $"Windows Sandbox 忙：另一个沙箱仍在运行，等待 {BusyWaitSeconds} 秒后放弃。本次没有启动新的虚拟机。",
                stopwatch.Elapsed);
        }

        try
        {
            return await ExecuteAsync(request, spec, stopwatch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 校验 spec 的目录就是 job 目录布局本身。这条检查是安全边界：
    /// work / output 在虚拟机里是可写的，如果把真实工作区（或任意宿主目录）映射进去，
    /// 沙箱就能直接改它。因此四个路径必须严格等于 {JobRoot}\input / work / output。
    /// </summary>
    private string? ValidateSpec(string jobId, WindowsSandboxJobSpec spec)
    {
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spec.JobRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"job 根目录无效：{spec.JobRoot}";
        }

        if (_directory is not null)
        {
            if (!_directory.IsUnderRoot(root, out var normalized))
            {
                return $"job 根目录不在沙箱根目录内：{spec.JobRoot}";
            }

            var expectedRoot = _directory.GetLayout(jobId).Root;
            if (!string.Equals(normalized, expectedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return $"job 根目录与 jobId 不一致：{spec.JobRoot}";
            }
        }

        var expected = new (string Name, string Path)[]
        {
            (SandboxDirectory.InputFolderName, spec.InputDirectory),
            (SandboxDirectory.WorkFolderName, spec.WorkDirectory),
            (SandboxDirectory.OutputFolderName, spec.OutputDirectory)
        };

        foreach (var (name, path) in expected)
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return $"映射目录无效：{path}";
            }

            if (!string.Equals(full, Path.Combine(root, name), StringComparison.OrdinalIgnoreCase))
            {
                return $"映射目录必须是 job 目录下的 {name}：{path}";
            }
        }

        if (Path.GetFileName(spec.ScriptFileName) != spec.ScriptFileName)
        {
            return "脚本文件名不能包含目录。";
        }

        return null;
    }

    private async Task<SandboxResult> ExecuteAsync(
        SandboxRequest request,
        WindowsSandboxJobSpec spec,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var limits = request.Limits;
        var resultPath = Path.Combine(spec.OutputDirectory, SandboxProtocol.ResultFileName);
        var startedPath = Path.Combine(spec.OutputDirectory, "started.txt");
        var stdoutPath = Path.Combine(spec.OutputDirectory, "stdout.txt");
        var stderrPath = Path.Combine(spec.OutputDirectory, "stderr.txt");
        var scriptPath = Path.Combine(spec.InputDirectory, spec.ScriptFileName);
        var runScriptPath = Path.Combine(spec.InputDirectory, WindowsSandboxRunScript.RunScriptFileName);
        var wrapperPath = Path.Combine(spec.InputDirectory, WindowsSandboxRunScript.ScriptWrapperFileName);
        var configurationPath = Path.Combine(spec.InputDirectory, WindowsSandboxConfig.ConfigurationFileName);

        IWindowsSandboxInstance? instance = null;
        try
        {
            Directory.CreateDirectory(spec.InputDirectory);
            Directory.CreateDirectory(spec.WorkDirectory);
            Directory.CreateDirectory(spec.OutputDirectory);

            // 上一次运行的残留会让“结果已经存在”误判成这次成功，先清掉。
            TryDelete(resultPath);
            TryDelete(startedPath);
            TryDelete(stdoutPath);
            TryDelete(stderrPath);

            if (!File.Exists(scriptPath))
            {
                return Failure(request.JobId, SandboxFailureCode.LaunchFailed,
                    $"脚本不存在：{scriptPath}", stopwatch.Elapsed);
            }

            File.WriteAllText(
                runScriptPath,
                WindowsSandboxRunScript.CreateRunner(request.JobId, spec.WorkingDirectory),
                new UTF8Encoding(false));

            File.WriteAllText(
                wrapperPath,
                WindowsSandboxRunScript.CreateWrapper(ScriptSandboxPath(spec.ScriptFileName)),
                new UTF8Encoding(true));

            WindowsSandboxConfig.Write(
                configurationPath,
                [
                    new WindowsSandboxMappedFolder(
                        spec.InputDirectory, WindowsSandboxConfig.InputSandboxPath, ReadOnly: true),
                    new WindowsSandboxMappedFolder(
                        spec.WorkDirectory, WindowsSandboxConfig.WorkSandboxPath, ReadOnly: false),
                    new WindowsSandboxMappedFolder(
                        spec.OutputDirectory, WindowsSandboxConfig.OutputSandboxPath, ReadOnly: false)
                ],
                spec.MemoryMB);

            try
            {
                instance = _launcher.Launch(configurationPath);
                TrackActive(instance);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException
                                           or UnauthorizedAccessException or InvalidOperationException)
            {
                return Failure(request.JobId, SandboxFailureCode.LaunchFailed,
                    $"启动 Windows Sandbox 失败：{ex.Message}", stopwatch.Elapsed);
            }

            var timeout = TimeSpan.FromSeconds(
                Math.Clamp(limits.TimeoutSeconds, 1, MaximumTimeoutSeconds)
                + Math.Clamp(spec.StartupGraceSeconds, 0, 120));

            var timedOut = false;
            var canceled = false;
            var state = ResultReadState.Missing;
            SandboxResultDocument? document = null;

            while (true)
            {
                state = TryReadResult(resultPath, request.JobId, out document);
                if (state is ResultReadState.Ready or ResultReadState.Invalid)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    timedOut = true;
                    break;
                }

                await Task.Delay(PollIntervalMilliseconds, CancellationToken.None).ConfigureAwait(false);
            }

            instance.Terminate();

            var stdout = ReadLimitedText(stdoutPath, limits.MaxOutputBytes, out var stdoutTruncated);
            var stderr = ReadLimitedText(stderrPath, limits.MaxOutputBytes, out var stderrTruncated);
            var started = File.Exists(startedPath);
            var duration = document is null
                ? stopwatch.Elapsed
                : TimeSpan.FromMilliseconds(document.DurationMs);

            if (state is ResultReadState.Invalid)
            {
                return Failure(request.JobId, SandboxFailureCode.ProtocolError,
                    "result.json 的协议版本或 jobId 不匹配。", stopwatch.Elapsed);
            }

            if (state is ResultReadState.Missing)
            {
                // 超时也分两种：沙箱根本没启动（功能 / 映射问题）还是启动了但脚本没跑完。
                var failure = timedOut
                    ? started
                        ? SandboxFailureCode.Timeout
                        : SandboxFailureCode.LaunchFailed
                    : canceled
                        ? SandboxFailureCode.Canceled
                        : started
                            ? SandboxFailureCode.HostCrashed
                            : SandboxFailureCode.LaunchFailed;

                var message = timedOut
                    ? started
                        ? "已关闭沙箱：沙箱启动了，但没有在超时前写出 result.json（脚本可能卡死或被策略终止）；以下是截止终止前的输出。"
                        : "已关闭沙箱：沙箱在超时前没有启动完成（功能可能被组策略禁用，或映射目录不可用）。"
                    : canceled
                        ? "已取消；沙箱已关闭。"
                        : started
                            ? "沙箱已启动，但没有写出 result.json（进程可能崩溃或被组策略终止）。"
                            : "沙箱没有启动成功：功能可能被组策略禁用，或映射目录不可用。";

                return new SandboxResult
                {
                    JobId = request.JobId,
                    Kind = Kind,
                    ExitCode = -1,
                    StdOut = stdout,
                    StdErr = stderr,
                    TimedOut = timedOut,
                    Canceled = canceled,
                    Failure = failure,
                    Artifacts = ["stdout.txt", "stderr.txt"],
                    Duration = stopwatch.Elapsed,
                    Message = message
                };
            }

            return new SandboxResult
            {
                JobId = request.JobId,
                Kind = Kind,
                ExitCode = document!.ExitCode,
                StdOut = stdout,
                StdErr = stderr,
                TimedOut = timedOut,
                Canceled = canceled,
                Failure = timedOut
                    ? SandboxFailureCode.Timeout
                    : canceled
                        ? SandboxFailureCode.Canceled
                        : SandboxFailureCode.None,
                Artifacts = document.Artifacts,
                Duration = duration,
                Message = document.Error
                          ?? (document.Truncated ? "输出已截断（超过 maxOutputBytes）。" : null)
                          ?? (stdoutTruncated || stderrTruncated
                              ? "…（输出已截断）"
                              : null)
            };
        }
        finally
        {
            UntrackActive(instance);
            instance?.Terminate();
            instance?.Dispose();
            TryDelete(configurationPath);
        }
    }

    private static void TrackActive(IWindowsSandboxInstance instance)
    {
        lock (ActiveGate)
        {
            ActiveInstances.Add(instance);
        }
    }

    private static void UntrackActive(IWindowsSandboxInstance? instance)
    {
        if (instance is null)
        {
            return;
        }

        lock (ActiveGate)
        {
            ActiveInstances.Remove(instance);
        }
    }

    private static void TerminateActiveInstances()
    {
        IWindowsSandboxInstance[] snapshot;
        lock (ActiveGate)
        {
            snapshot = [.. ActiveInstances];
            ActiveInstances.Clear();
        }

        foreach (var instance in snapshot)
        {
            try
            {
                instance.Terminate();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // 退出路径上不再抛出。
            }
        }
    }

    private static string ScriptSandboxPath(string scriptFileName) =>
        WindowsSandboxConfig.InputSandboxPath + "\\" + scriptFileName;

    private enum ResultReadState
    {
        Missing,
        Ready,
        Invalid
    }

    private static ResultReadState TryReadResult(
        string path,
        string jobId,
        out SandboxResultDocument? document)
    {
        document = null;

        try
        {
            if (!File.Exists(path))
            {
                return ResultReadState.Missing;
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<SandboxResultDocument>(json, SandboxJson.Options);
            if (parsed is null)
            {
                return ResultReadState.Missing;
            }

            if (parsed.ProtocolVersion != SandboxProtocol.Version
                || !string.Equals(parsed.JobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                return ResultReadState.Invalid;
            }

            document = parsed;
            return ResultReadState.Ready;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 映射目录的写入有延迟：文件可能刚创建、内容还没写完，下一轮再读。
            return ResultReadState.Missing;
        }
    }

    private static string ReadLimitedText(string path, int maxBytes, out bool truncated)
    {
        truncated = false;
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结果；S6 由 SandboxJanitor 统一处理。
        }
    }

    private static SandboxResult Canceled(string jobId, TimeSpan duration) =>
        new()
        {
            JobId = jobId,
            Kind = SandboxKind.WindowsSandbox,
            ExitCode = -1,
            Canceled = true,
            Failure = SandboxFailureCode.Canceled,
            Duration = duration,
            Message = "已取消；沙箱未启动。"
        };

    private static SandboxResult Failure(
        string jobId,
        SandboxFailureCode failure,
        string message,
        TimeSpan duration) =>
        new()
        {
            JobId = jobId,
            Kind = SandboxKind.WindowsSandbox,
            ExitCode = -1,
            Failure = failure,
            Message = message,
            Duration = duration
        };
}
