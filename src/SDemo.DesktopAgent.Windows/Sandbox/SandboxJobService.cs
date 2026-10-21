using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱作业服务（S2/S4）：工作区快照 → 写 job.json / script.ps1 → 按策略选择沙箱执行 →
/// 读 result.json → 计算变更集 → 通过 <see cref="OutcomeAvailable"/> 交给 UI。
/// 沙箱进程只能写 work / output，真实工作区只在用户确认后由 <c>ChangeApplier</c> 写回。
///
/// S4 起有两类执行后端，作业准备方式不同，但快照 / 变更集 / 审批流程完全复用：
/// AppContainer 用 SandboxHost + Package SID ACL；Windows Sandbox 用映射目录 + run.ps1。
/// </summary>
public sealed class SandboxJobService : ISandboxJobService
{
    private const int MaxScriptBytes = 512 * 1024;
    private const int HostConsoleOutputBytes = 1024 * 1024;

    /// <summary>Windows Sandbox 自检脚本：验证能跑 PowerShell，并且真的没有网络。</summary>
    private const string WindowsSandboxSelfTestScript = """
        Write-Output 'SDEMO_WSB_OK'
        try {
            Invoke-WebRequest -UseBasicParsing -Uri 'https://example.com' -TimeoutSec 5 | Out-Null
            Write-Output 'SDEMO_WSB_NET=allowed'
        } catch {
            Write-Output 'SDEMO_WSB_NET=blocked'
        }
        """;

    private readonly ISandboxSelector _selector;
    private readonly SandboxProbe _probe;
    private readonly AppContainerProfileProvider _profiles;
    private readonly SandboxDirectory _directory;
    private readonly WorkspaceContext _workspace;
    private readonly ISettingsService _settings;
    private readonly SandboxHostInstaller _hostInstaller;
    private readonly IReadOnlyList<ISandboxRunner> _runners;
    private readonly WindowsSandboxRunner _windowsSandbox;

    public SandboxJobService(
        ISandboxSelector selector,
        SandboxProbe probe,
        AppContainerProfileProvider profiles,
        SandboxDirectory directory,
        WorkspaceContext workspace,
        ISettingsService settings,
        SandboxHostInstaller hostInstaller,
        IEnumerable<ISandboxRunner> runners,
        WindowsSandboxRunner windowsSandbox)
    {
        _selector = selector;
        _probe = probe;
        _profiles = profiles;
        _directory = directory;
        _workspace = workspace;
        _settings = settings;
        _hostInstaller = hostInstaller;
        _runners = runners.ToArray();
        _windowsSandbox = windowsSandbox;
    }

    public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;

    /// <summary>当前策略下实际会用的沙箱可用性（含 Verified），工具与设置页都以此为准。</summary>
    public async Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current.Sandbox;
        var selection = await _selector.SelectAsync(
                new SandboxPolicy(settings.Requirement, settings.Preferred, settings.WorkspaceReadOnly),
                requireVerified: false,
                cancellationToken)
            .ConfigureAwait(false);

        return selection.Availability;
    }

    public async Task<SandboxAvailability> GetAvailabilityAsync(
        SandboxKind kind,
        CancellationToken cancellationToken = default)
    {
        var runner = _runners.FirstOrDefault(candidate => candidate.Kind == kind);
        if (runner is null)
        {
            return new SandboxAvailability(
                kind,
                Available: false,
                Verified: false,
                Reason: "当前平台没有这个沙箱运行器。",
                Detail: null);
        }

        return await runner.ProbeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default)
    {
        var report = await _probe.SelfTestAsync(cancellationToken).ConfigureAwait(false);
        _probe.ResetCache();
        return report;
    }

    /// <summary>
    /// Windows Sandbox 自检：真的启动一次虚拟机（约 10–30 秒）。只有全部通过才把运行器标记为
    /// Verified，PowerShell 工具随后才允许使用它。
    /// </summary>
    public async Task<WindowsSandboxSelfTestReport> RunWindowsSandboxSelfTestAsync(
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var availability = await _windowsSandbox.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.Available)
        {
            var reason = string.IsNullOrWhiteSpace(availability.Detail)
                ? availability.Reason
                : $"{availability.Reason} {availability.Detail}";

            return new WindowsSandboxSelfTestReport(
                Launched: false,
                PowerShellOk: false,
                NoNetwork: false,
                stopwatch.Elapsed,
                reason);
        }

        var jobId = $"wsbtest-{Guid.NewGuid():N}"[..16];
        SandboxJobLayout? layout = null;

        try
        {
            layout = _directory.CreateJob(jobId);
            await File.WriteAllTextAsync(
                    Path.Combine(layout.InputDirectory, "script.ps1"),
                    WindowsSandboxSelfTestScript,
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);

            var result = await _windowsSandbox.RunAsync(
                    new SandboxRequest
                    {
                        JobId = jobId,
                        Kind = SandboxKind.WindowsSandbox,
                        Executable = WindowsSandboxAvailability.ReadHostInfo().ExecutablePath,
                        WorkingDirectory = layout.WorkDirectory,
                        OutputDirectory = layout.OutputDirectory,
                        Limits = new SandboxLimits
                        {
                            TimeoutSeconds = 60,
                            MaxOutputBytes = 256 * 1024
                        },
                        WindowsSandbox = new WindowsSandboxJobSpec
                        {
                            JobRoot = layout.Root,
                            InputDirectory = layout.InputDirectory,
                            WorkDirectory = layout.WorkDirectory,
                            OutputDirectory = layout.OutputDirectory,
                            MemoryMB = _settings.Current.Sandbox.WindowsSandboxMemoryMB
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var launched = result.Failure == SandboxFailureCode.None && result.ExitCode == 0;
            var powershellOk = result.StdOut.Contains("SDEMO_WSB_OK", StringComparison.Ordinal);
            var noNetwork = result.StdOut.Contains("SDEMO_WSB_NET=blocked", StringComparison.Ordinal);

            WindowsSandboxSelfTestReport report;
            if (launched && powershellOk && noNetwork)
            {
                report = new WindowsSandboxSelfTestReport(
                    true, true, true, stopwatch.Elapsed, Detail: null);
            }
            else
            {
                var detail = new List<string>();
                if (!launched)
                {
                    detail.Add($"启动 / 执行失败：exit={result.ExitCode}，failure={result.Failure}"
                               + (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $"，{result.Message}"));
                }

                if (launched && !powershellOk)
                {
                    detail.Add("PowerShell 没有输出预期标记。");
                }

                if (launched && !noNetwork)
                {
                    detail.Add(result.StdOut.Contains("SDEMO_WSB_NET=allowed", StringComparison.Ordinal)
                        ? "沙箱内可以访问网络：Networking=Disable 未生效。"
                        : "无法确认网络是否被禁用。");
                }

                var stderr = result.StdErr.Trim();
                if (stderr.Length > 0)
                {
                    detail.Add($"stderr={Truncate(stderr, 400)}");
                }

                report = new WindowsSandboxSelfTestReport(
                    launched, powershellOk, noNetwork, stopwatch.Elapsed, string.Join("；", detail));
            }

            _windowsSandbox.MarkVerified(report.AllPassed);
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            _windowsSandbox.MarkVerified(false);
            return new WindowsSandboxSelfTestReport(
                false, false, false, stopwatch.Elapsed, $"自检执行失败：{ex.Message}");
        }
        finally
        {
            if (layout is not null)
            {
                _directory.DeleteJob(jobId);
            }
        }
    }

    public async Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current.Sandbox;

        if (!settings.Enabled)
        {
            return Failed(sessionId, SandboxFailureCode.NotAvailable, "执行沙箱已在设置中关闭。");
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return Failed(sessionId, SandboxFailureCode.ProtocolError, "PowerShell 脚本为空。");
        }

        if (Encoding.UTF8.GetByteCount(script) > MaxScriptBytes)
        {
            return Failed(sessionId, SandboxFailureCode.ProtocolError, "PowerShell 脚本超过 512KB 上限。");
        }

        var policy = new SandboxPolicy(settings.Requirement, settings.Preferred, settings.WorkspaceReadOnly);

        // PowerShell 必须 Available && Verified：能启动进程不等于能跑脚本。
        var selection = await _selector.SelectAsync(policy, requireVerified: true, cancellationToken)
            .ConfigureAwait(false);

        if (!selection.IsAvailable || !selection.Availability.Verified)
        {
            return Failed(
                sessionId,
                SandboxFailureCode.NotAvailable,
                selection.Availability.Reason ?? "沙箱不可用。",
                kind: selection.Availability.Kind);
        }

        var kind = selection.Availability.Kind;

        AppContainerProfile? profile = null;
        if (kind == SandboxKind.AppContainer)
        {
            profile = _profiles.TryGet(out var profileReason);
            if (profile is null)
            {
                return Failed(
                    sessionId,
                    SandboxFailureCode.NotAvailable,
                    profileReason ?? "AppContainer profile 不可用。");
            }
        }
        else if (kind != SandboxKind.WindowsSandbox)
        {
            return Failed(
                sessionId,
                SandboxFailureCode.NotAvailable,
                $"策略选中的沙箱 {kind} 不支持 PowerShell。",
                kind: kind);
        }

        var workspace = ResolveWorkspaceRoot(workspaceRoot);
        if (workspace is null)
        {
            return Failed(
                sessionId,
                SandboxFailureCode.PathEscape,
                "工作区无效：必须是已存在、非磁盘根目录的本地目录。");
        }

        if (SandboxPathGuard.IsUnder(_directory.Root, workspace))
        {
            return Failed(sessionId, SandboxFailureCode.PathEscape, "工作区不能位于沙箱根目录内。");
        }

        var workingDirectoryError = ResolveJobWorkingDirectory(
            workspace,
            workingDirectory,
            out var jobWorkingDirectory);
        if (workingDirectoryError is not null)
        {
            return Failed(sessionId, SandboxFailureCode.PathEscape, workingDirectoryError);
        }

        var jobId = NewJobId();
        var timeout = Math.Clamp(
            timeoutSeconds <= 0 ? settings.TimeoutSeconds : timeoutSeconds,
            1,
            120);
        var maxOutputBytes = 8 * 1024 * 1024;

        SandboxJobLayout layout;
        try
        {
            if (kind == SandboxKind.WindowsSandbox)
            {
                // 映射目录由 Windows Sandbox 直接使用，不需要 Package SID ACL，也不需要 home 目录。
                layout = _directory.CreateJob(jobId);
            }
            else
            {
                _directory.EnsureRoot(profile!.Sid);
                layout = _directory.EnsureJob(jobId, profile.Sid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return Failed(sessionId, SandboxFailureCode.LaunchFailed, $"准备沙箱目录失败：{ex.Message}");
        }

        var baseDirectory = Path.Combine(layout.Root, SandboxDirectory.BaseFolderName);
        WorkspaceManifest manifest;
        try
        {
            manifest = await WorkspaceSnapshot.CreateAsync(
                    workspace,
                    baseDirectory,
                    layout.WorkDirectory,
                    new WorkspaceCopyOptions
                    {
                        MaxBytes = Math.Max(1, settings.WorkspaceCopyLimitMB) * 1024L * 1024,
                        MaxFiles = 20_000
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkspaceTooLargeException ex)
        {
            return Failed(sessionId, SandboxFailureCode.WorkspaceTooLarge, ex.Message, jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(sessionId, SandboxFailureCode.LaunchFailed, $"复制工作区失败：{ex.Message}", jobId);
        }

        try
        {
            manifest.Save(Path.Combine(layout.Root, SandboxProtocol.ManifestFileName));

            var scriptPath = Path.Combine(layout.InputDirectory, "script.ps1");
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken)
                .ConfigureAwait(false);

            var job = new SandboxJobDocument
            {
                JobId = jobId,
                Mode = SandboxJobMode.PowerShell,
                ScriptFile = "input/script.ps1",
                WorkingDirectory = jobWorkingDirectory,
                TimeoutSeconds = timeout,
                MaxOutputBytes = maxOutputBytes
            };
            WriteJsonAtomic(Path.Combine(layout.InputDirectory, SandboxProtocol.JobFileName), job);

            var request = kind == SandboxKind.WindowsSandbox
                ? CreateWindowsSandboxRequest(jobId, layout, jobWorkingDirectory, timeout, maxOutputBytes, settings)
                : await CreateAppContainerRequestAsync(
                        jobId, layout, timeout, settings, cancellationToken).ConfigureAwait(false);

            var run = await selection.Runner!.RunAsync(request, cancellationToken).ConfigureAwait(false);

            var result = ReadResult(layout, jobId, run);
            var changes = await WorkspaceDiff.ComputeAsync(
                    layout.WorkDirectory,
                    baseDirectory,
                    manifest,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var outcome = new SandboxJobOutcome(sessionId, jobId, result, changes, layout.Root)
            {
                WorkspaceRoot = workspace
            };
            OutcomeAvailable?.Invoke(this, outcome);
            return outcome;
        }
        catch (SandboxProtocolException ex)
        {
            return Failed(sessionId, SandboxFailureCode.ProtocolError, ex.Message, jobId, kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(sessionId, SandboxFailureCode.LaunchFailed, ex.Message, jobId, kind);
        }
    }

    private static SandboxRequest CreateWindowsSandboxRequest(
        string jobId,
        SandboxJobLayout layout,
        string jobWorkingDirectory,
        int timeout,
        int maxOutputBytes,
        SandboxSettings settings) =>
        new()
        {
            JobId = jobId,
            Kind = SandboxKind.WindowsSandbox,
            // Windows Sandbox 的执行入口是 .wsb 里的 LogonCommand；Executable 只用于诊断展示。
            Executable = WindowsSandboxAvailability.ReadHostInfo().ExecutablePath,
            WorkingDirectory = layout.WorkDirectory,
            OutputDirectory = layout.OutputDirectory,
            Limits = new SandboxLimits
            {
                // 虚拟机启动时间由运行器额外加宽限，这里的超时是脚本本身的超时。
                TimeoutSeconds = timeout,
                MemoryLimitMB = settings.MemoryLimitMB,
                JobMemoryLimitMB = settings.JobMemoryLimitMB,
                MaxProcesses = settings.MaxProcesses,
                MaxOutputBytes = maxOutputBytes
            },
            WindowsSandbox = new WindowsSandboxJobSpec
            {
                JobRoot = layout.Root,
                InputDirectory = layout.InputDirectory,
                WorkDirectory = layout.WorkDirectory,
                OutputDirectory = layout.OutputDirectory,
                WorkingDirectory = jobWorkingDirectory,
                MemoryMB = settings.WindowsSandboxMemoryMB
            }
        };

    private async Task<SandboxRequest> CreateAppContainerRequestAsync(
        string jobId,
        SandboxJobLayout layout,
        int timeout,
        SandboxSettings settings,
        CancellationToken cancellationToken)
    {
        var hostPath = await _hostInstaller.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);

        return new SandboxRequest
        {
            JobId = jobId,
            Kind = SandboxKind.AppContainer,
            Executable = hostPath,
            Arguments = ["run", "--job", Path.Combine(layout.InputDirectory, SandboxProtocol.JobFileName)],
            WorkingDirectory = layout.WorkDirectory,
            OutputDirectory = layout.OutputDirectory,
            Limits = new SandboxLimits
            {
                TimeoutSeconds = timeout + 30,
                MemoryLimitMB = settings.MemoryLimitMB,
                JobMemoryLimitMB = settings.JobMemoryLimitMB,
                MaxProcesses = settings.MaxProcesses,
                MaxOutputBytes = HostConsoleOutputBytes
            }
        };
    }

    public async Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        var availability = await GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        var reason = availability.Reason ?? "沙箱不可用。";

        if (!availability.Available || !availability.Verified)
        {
            return new PdfExtraction(
                Ok: false,
                PageCount: 0,
                Encrypted: false,
                HasTextLayer: false,
                Pages: [],
                Truncated: false,
                Message: reason);
        }

        // S5 实现：把 PDF 复制到 input、由 SandboxHost 解析、读回 pdf.json。
        throw new NotSupportedException("沙箱 PDF 解析在 S5 阶段实现。");
    }

    private string? ResolveWorkspaceRoot(string? workspaceRoot)
    {
        var candidate = string.IsNullOrWhiteSpace(workspaceRoot)
            ? _workspace.RootPath
            : workspaceRoot;

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!Directory.Exists(fullPath)
                || Path.TrimEndingDirectorySeparator(fullPath)
                    .Equals(Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把工具传入的相对目录解析成 job.json 里的 <c>work/...</c> 相对路径。
    /// 只接受工作区内的相对路径：拒绝绝对路径、UNC、<c>..</c>、符号链接 / junction。
    /// </summary>
    private static string? ResolveJobWorkingDirectory(
        string workspaceRoot,
        string? workingDirectory,
        out string jobWorkingDirectory)
    {
        jobWorkingDirectory = "work";

        var value = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory.Trim();

        if (value.Contains('\0')
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            return $"工作目录必须是工作区内的相对路径：{workingDirectory}";
        }

        var segments = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".."))
        {
            return $"工作目录不能包含 ..：{workingDirectory}";
        }

        var full = Path.GetFullPath(
            Path.Combine(workspaceRoot, string.Join(Path.DirectorySeparatorChar, segments)));

        if (!SandboxPathGuard.IsUnder(workspaceRoot, full))
        {
            return $"工作目录超出工作区范围：{workingDirectory}";
        }

        if (!Directory.Exists(full))
        {
            return $"工作目录不存在：{workingDirectory}";
        }

        var current = workspaceRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return $"工作目录包含符号链接或 junction：{workingDirectory}";
            }
        }

        if (segments.Length > 0)
        {
            jobWorkingDirectory = "work/" + string.Join('/', segments);
        }

        return null;
    }

    private static SandboxResult ReadResult(
        SandboxJobLayout layout,
        string jobId,
        SandboxResult run)
    {
        var resultPath = Path.Combine(layout.OutputDirectory, SandboxProtocol.ResultFileName);
        if (!File.Exists(resultPath))
        {
            return new SandboxResult
            {
                JobId = jobId,
                Kind = run.Kind,
                ExitCode = run.ExitCode,
                TimedOut = run.TimedOut,
                Canceled = run.Canceled,
                Failure = run.Failure == SandboxFailureCode.None
                    ? SandboxFailureCode.HostCrashed
                    : run.Failure,
                StdOut = run.StdOut,
                StdErr = run.StdErr,
                Duration = run.Duration,
                Message = run.Message ?? "沙箱没有写出 result.json。"
            };
        }

        try
        {
            var json = File.ReadAllText(resultPath);
            var document = JsonSerializer.Deserialize<SandboxResultDocument>(json, SandboxJson.Options);
            if (document is null
                || document.ProtocolVersion != SandboxProtocol.Version
                || !string.Equals(document.JobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                return new SandboxResult
                {
                    JobId = jobId,
                    Kind = run.Kind,
                    ExitCode = -1,
                    Failure = SandboxFailureCode.ProtocolError,
                    Message = "result.json 的协议版本或 jobId 不匹配。",
                    Duration = run.Duration
                };
            }

            var failure = document.Error is not null
                ? SandboxFailureCode.ProtocolError
                : run.Failure;

            return new SandboxResult
            {
                JobId = jobId,
                Kind = run.Kind,
                ExitCode = document.ExitCode,
                TimedOut = document.TimedOut || run.TimedOut,
                Canceled = run.Canceled,
                Failure = failure,
                StdOut = document.StdOut.Length > 0 ? document.StdOut : run.StdOut,
                StdErr = document.StdErr.Length > 0 ? document.StdErr : run.StdErr,
                Artifacts = document.Artifacts,
                Duration = TimeSpan.FromMilliseconds(document.DurationMs),
                Message = document.Error
                          ?? (document.Truncated ? "输出已截断（超过 maxOutputBytes）。" : run.Message)
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SandboxResult
            {
                JobId = jobId,
                Kind = run.Kind,
                ExitCode = -1,
                Failure = SandboxFailureCode.ProtocolError,
                Message = $"读取 result.json 失败：{ex.Message}",
                Duration = run.Duration
            };
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, SandboxJson.Options);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string NewJobId() =>
        $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..22];

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static SandboxJobOutcome Failed(
        string sessionId,
        SandboxFailureCode failure,
        string message,
        string jobId = "",
        SandboxKind kind = SandboxKind.AppContainer) =>
        new(
            sessionId,
            jobId,
            new SandboxResult
            {
                JobId = jobId,
                Kind = kind,
                ExitCode = -1,
                Failure = failure,
                Message = message
            },
            [],
            string.Empty);
}
