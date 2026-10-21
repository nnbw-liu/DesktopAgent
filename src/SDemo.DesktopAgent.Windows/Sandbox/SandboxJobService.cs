using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱作业服务（S2）：工作区快照 → 写 job.json / script.ps1 → 启动 SandboxHost →
/// 读 result.json → 计算变更集 → 通过 <see cref="OutcomeAvailable"/> 交给 UI。
/// 沙箱进程只能写 work / output，真实工作区只在用户确认后由 <c>ChangeApplier</c> 写回。
/// </summary>
public sealed class SandboxJobService : ISandboxJobService
{
    private const int MaxScriptBytes = 512 * 1024;
    private const int HostConsoleOutputBytes = 1024 * 1024;

    private readonly ISandboxSelector _selector;
    private readonly SandboxProbe _probe;
    private readonly AppContainerProfileProvider _profiles;
    private readonly SandboxDirectory _directory;
    private readonly WorkspaceContext _workspace;
    private readonly ISettingsService _settings;
    private readonly SandboxHostInstaller _hostInstaller;

    public SandboxJobService(
        ISandboxSelector selector,
        SandboxProbe probe,
        AppContainerProfileProvider profiles,
        SandboxDirectory directory,
        WorkspaceContext workspace,
        ISettingsService settings,
        SandboxHostInstaller hostInstaller)
    {
        _selector = selector;
        _probe = probe;
        _profiles = profiles;
        _directory = directory;
        _workspace = workspace;
        _settings = settings;
        _hostInstaller = hostInstaller;
    }

    public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        _selector.GetAvailabilityAsync(cancellationToken);

    public async Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default)
    {
        var report = await _probe.SelfTestAsync(cancellationToken).ConfigureAwait(false);
        _probe.ResetCache();
        return report;
    }

    public async Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
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

        var availability = await _selector.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.Available || !availability.Verified)
        {
            return Failed(
                sessionId,
                SandboxFailureCode.NotAvailable,
                availability.Reason ?? "沙箱不可用。");
        }

        var profile = _profiles.TryGet(out var profileReason);
        if (profile is null)
        {
            return Failed(sessionId, SandboxFailureCode.NotAvailable, profileReason ?? "AppContainer profile 不可用。");
        }

        var workspaceRoot = ResolveWorkspaceRoot(workingDirectory);
        if (workspaceRoot is null)
        {
            return Failed(
                sessionId,
                SandboxFailureCode.PathEscape,
                "工作目录无效：必须是已存在、非磁盘根目录的本地目录。");
        }

        if (SandboxPathGuard.IsUnder(_directory.Root, workspaceRoot))
        {
            return Failed(sessionId, SandboxFailureCode.PathEscape, "工作区不能位于沙箱根目录内。");
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
            _directory.EnsureRoot(profile.Sid);
            layout = _directory.EnsureJob(jobId, profile.Sid);
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
                    workspaceRoot,
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
                WorkingDirectory = "work",
                TimeoutSeconds = timeout,
                MaxOutputBytes = maxOutputBytes
            };
            WriteJsonAtomic(Path.Combine(layout.InputDirectory, SandboxProtocol.JobFileName), job);

            var hostPath = await _hostInstaller.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);

            var request = new SandboxRequest
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

            var run = await _selector.RunAsync(
                    request,
                    new SandboxPolicy(settings.Requirement, settings.Preferred, settings.WorkspaceReadOnly),
                    cancellationToken)
                .ConfigureAwait(false);

            var result = ReadResult(layout, jobId, run);
            var changes = await WorkspaceDiff.ComputeAsync(
                    layout.WorkDirectory,
                    baseDirectory,
                    manifest,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var outcome = new SandboxJobOutcome(sessionId, jobId, result, changes, layout.Root)
            {
                WorkspaceRoot = workspaceRoot
            };
            OutcomeAvailable?.Invoke(this, outcome);
            return outcome;
        }
        catch (SandboxProtocolException ex)
        {
            return Failed(sessionId, SandboxFailureCode.ProtocolError, ex.Message, jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(sessionId, SandboxFailureCode.LaunchFailed, ex.Message, jobId);
        }
    }

    public async Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        var availability = await _selector.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
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

    private string? ResolveWorkspaceRoot(string? workingDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(workingDirectory)
            ? _workspace.RootPath
            : workingDirectory;

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!Directory.Exists(fullPath)
                || Path.TrimEndingDirectorySeparator(fullPath)
                    .Equals(Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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
                Message = run.Message ?? "SandboxHost 没有写出 result.json。"
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

    private static SandboxJobOutcome Failed(
        string sessionId,
        SandboxFailureCode failure,
        string message,
        string jobId = "") =>
        new(
            sessionId,
            jobId,
            new SandboxResult
            {
                JobId = jobId,
                Kind = SandboxKind.AppContainer,
                ExitCode = -1,
                Failure = failure,
                Message = message
            },
            [],
            string.Empty);
}
