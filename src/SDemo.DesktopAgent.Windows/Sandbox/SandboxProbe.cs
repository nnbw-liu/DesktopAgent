using System.IO;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱探针：可用性探测（cmd + PowerShell，缓存 5 分钟）与设置页“执行沙箱”自检。
/// 自检里 CannotReadUserProfile / CannotWriteWorkspace / NoNetwork 为 true 表示“被拒绝”，即隔离生效。
/// </summary>
public sealed class SandboxProbe
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private const string PowerShellPath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    private readonly ISandboxRunner _runner;
    private readonly SandboxDirectory _directory;
    private readonly AppContainerProfileProvider _profiles;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private SandboxAvailability? _cached;
    private DateTimeOffset _cachedAt;

    public SandboxProbe(
        ISandboxRunner runner,
        SandboxDirectory directory,
        AppContainerProfileProvider profiles,
        IAppPaths paths,
        ISettingsService settings)
    {
        _runner = runner;
        _directory = directory;
        _profiles = profiles;
        _paths = paths;
        _settings = settings;
    }

    public async Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cached;
            }
        }

        var availability = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _cached = availability;
            _cachedAt = DateTimeOffset.UtcNow;
        }

        return availability;
    }

    /// <summary>丢弃缓存（例如用户点击“重新自检”）。</summary>
    public void ResetCache()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private async Task<SandboxAvailability> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        var profile = _profiles.TryGet(out var reason);
        if (profile is null)
        {
            return new SandboxAvailability(
                SandboxKind.AppContainer,
                Available: false,
                Verified: false,
                Reason: reason ?? "AppContainer profile 不可用。",
                Detail: reason);
        }

        var jobId = NewJobId("probe");
        try
        {
            _directory.EnsureRoot(profile.Sid);
            var layout = _directory.EnsureJob(jobId, profile.Sid);

            var cmd = await RunAsync(
                    layout,
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    ["/c", "echo SDEMO_SANDBOX_OK"],
                    Limit(timeoutSeconds: 15),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsSuccessful(cmd) || !cmd.StdOut.Contains("SDEMO_SANDBOX_OK", StringComparison.Ordinal))
            {
                return new SandboxAvailability(
                    SandboxKind.AppContainer,
                    Available: false,
                    Verified: false,
                    Reason: "基础进程无法在 AppContainer 内启动。",
                    Detail: Describe(cmd));
            }

            var powershell = await RunAsync(
                    layout,
                    PowerShellPath,
                    PowerShellArguments(
                        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); " +
                        "$OutputEncoding=[Console]::OutputEncoding; Write-Output 'SDEMO_PS_OK'"),
                    Limit(timeoutSeconds: 30),
                    cancellationToken)
                .ConfigureAwait(false);

            var powershellOk = IsSuccessful(powershell)
                               && powershell.StdOut.Contains("SDEMO_PS_OK", StringComparison.Ordinal);

            return new SandboxAvailability(
                SandboxKind.AppContainer,
                Available: true,
                Verified: powershellOk,
                Reason: powershellOk ? null : "PowerShell 在 AppContainer 内不可用。",
                Detail: powershellOk ? null : Describe(powershell));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return new SandboxAvailability(
                SandboxKind.AppContainer,
                Available: false,
                Verified: false,
                Reason: "沙箱探针执行失败。",
                Detail: ex.Message);
        }
        finally
        {
            _directory.DeleteJob(jobId);
        }
    }

    /// <summary>设置页自检：对应的“符合预期”全部为 true 时 AllPassed 为 true。</summary>
    public async Task<SandboxProbeReport> SelfTestAsync(CancellationToken cancellationToken = default)
    {
        var profile = _profiles.TryGet(out var profileReason);
        if (profile is null)
        {
            return new SandboxProbeReport(
                false, false, false, false, false, false, false,
                profileReason ?? "AppContainer profile 不可用。");
        }

        var details = new List<string>();
        var cmdOk = false;
        var powershellOk = false;
        var cannotReadUserProfile = false;
        var cannotWriteWorkspace = false;
        var noNetwork = false;
        var subprocessOk = false;
        var timeoutOk = false;

        var jobId = NewJobId("selftest");
        try
        {
            _directory.EnsureRoot(profile.Sid);
            var layout = _directory.EnsureJob(jobId, profile.Sid);
            var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

            var cmd = await RunAsync(layout, cmdPath, ["/c", "echo SDEMO_SANDBOX_OK"], Limit(15), cancellationToken)
                .ConfigureAwait(false);
            cmdOk = IsSuccessful(cmd) && cmd.StdOut.Contains("SDEMO_SANDBOX_OK", StringComparison.Ordinal);
            if (!cmdOk)
            {
                details.Add($"基本进程：{Describe(cmd)}");
            }

            var powershell = await RunAsync(
                    layout,
                    PowerShellPath,
                    PowerShellArguments(
                        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); " +
                        "$OutputEncoding=[Console]::OutputEncoding; Write-Output 'SDEMO_PS_OK'; Write-Output '中文'"),
                    Limit(30),
                    cancellationToken)
                .ConfigureAwait(false);
            powershellOk = IsSuccessful(powershell)
                           && powershell.StdOut.Contains("SDEMO_PS_OK", StringComparison.Ordinal)
                           && powershell.StdOut.Contains("中文", StringComparison.Ordinal);
            if (!powershellOk)
            {
                details.Add($"PowerShell：{Describe(powershell)}");
            }

            var userSentinel = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "sdemo-sandbox-sentinel.txt");
            try
            {
                File.WriteAllText(userSentinel, "SENTINEL_SECRET");
                var read = await RunAsync(
                        layout,
                        cmdPath,
                        ["/c", $"type \"{userSentinel}\""],
                        Limit(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                cannotReadUserProfile = read.Failure == SandboxFailureCode.None
                                        && read.ExitCode != 0
                                        && !read.StdOut.Contains("SENTINEL_SECRET", StringComparison.Ordinal);
                if (!cannotReadUserProfile)
                {
                    details.Add($"读用户目录：{Describe(read)}");
                }
            }
            finally
            {
                TryDeleteFile(userSentinel);
            }

            var workspace = _paths.DefaultWorkspaceRoot;
            Directory.CreateDirectory(workspace);
            var workspaceSentinel = Path.Combine(workspace, "sdemo-sandbox-sentinel.txt");
            try
            {
                File.WriteAllText(workspaceSentinel, "SENTINEL_WORKSPACE");
                var write = await RunAsync(
                        layout,
                        cmdPath,
                        ["/c", $"echo x > \"{workspaceSentinel}\""],
                        Limit(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                cannotWriteWorkspace = write.Failure == SandboxFailureCode.None && write.ExitCode != 0;
                if (!cannotWriteWorkspace)
                {
                    details.Add($"写工作区：{Describe(write)}");
                }
            }
            finally
            {
                TryDeleteFile(workspaceSentinel);
            }

            var network = await RunAsync(
                    layout,
                    PowerShellPath,
                    PowerShellArguments(
                        "try { Invoke-WebRequest -UseBasicParsing -Uri 'https://example.com' -TimeoutSec 3 | Out-Null; exit 0 } catch { exit 1 }"),
                    Limit(20),
                    cancellationToken)
                .ConfigureAwait(false);
            noNetwork = network.Failure == SandboxFailureCode.None && network.ExitCode != 0;
            if (!noNetwork)
            {
                details.Add($"联网：{Describe(network)}");
            }

            var subprocess = await RunAsync(
                    layout,
                    PowerShellPath,
                    PowerShellArguments(
                        "& \"$env:SystemRoot\\System32\\cmd.exe\" /c \"exit 0\"; exit $LASTEXITCODE"),
                    Limit(20),
                    cancellationToken)
                .ConfigureAwait(false);
            subprocessOk = IsSuccessful(subprocess);
            if (!subprocessOk)
            {
                details.Add($"子进程：{Describe(subprocess)}");
            }

            var timeout = await RunAsync(
                    layout,
                    PowerShellPath,
                    PowerShellArguments("Start-Sleep -Seconds 30"),
                    Limit(timeoutSeconds: 3),
                    cancellationToken)
                .ConfigureAwait(false);
            timeoutOk = timeout.TimedOut || timeout.Failure == SandboxFailureCode.Timeout;
            if (!timeoutOk)
            {
                details.Add($"超时终止：{Describe(timeout)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            details.Add($"自检执行失败：{ex.Message}");
        }
        finally
        {
            _directory.DeleteJob(jobId);
        }

        return new SandboxProbeReport(
            cmdOk,
            powershellOk,
            cannotReadUserProfile,
            cannotWriteWorkspace,
            noNetwork,
            subprocessOk,
            timeoutOk,
            details.Count == 0 ? null : string.Join("；", details));
    }

    private Task<SandboxResult> RunAsync(
        SandboxJobLayout layout,
        string executable,
        IReadOnlyList<string> arguments,
        SandboxLimits limits,
        CancellationToken cancellationToken) =>
        _runner.RunAsync(
            new SandboxRequest
            {
                JobId = layout.JobId,
                Kind = SandboxKind.AppContainer,
                WorkingDirectory = layout.WorkDirectory,
                OutputDirectory = layout.OutputDirectory,
                Executable = executable,
                Arguments = arguments,
                Limits = limits
            },
            cancellationToken);

    private SandboxLimits Limit(int timeoutSeconds)
    {
        var configured = _settings.Current.Sandbox;
        return new SandboxLimits
        {
            TimeoutSeconds = timeoutSeconds,
            MemoryLimitMB = configured.MemoryLimitMB,
            JobMemoryLimitMB = configured.JobMemoryLimitMB,
            MaxProcesses = configured.MaxProcesses,
            MaxOutputBytes = 128 * 1024
        };
    }

    private static IReadOnlyList<string> PowerShellArguments(string script) =>
    [
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        script
    ];

    private static bool IsSuccessful(SandboxResult result) =>
        result.Failure == SandboxFailureCode.None && !result.TimedOut && result.ExitCode == 0;

    private static string Describe(SandboxResult result)
    {
        var parts = new List<string> { $"exit={result.ExitCode}" };
        if (result.Failure != SandboxFailureCode.None)
        {
            parts.Add($"failure={result.Failure}");
        }

        if (result.TimedOut)
        {
            parts.Add("timedOut=true");
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            parts.Add(result.Message!);
        }

        var stderr = result.StdErr.Trim();
        if (stderr.Length > 0)
        {
            parts.Add($"stderr={Truncate(stderr, 160)}");
        }

        var stdout = result.StdOut.Trim();
        if (stdout.Length > 0)
        {
            parts.Add($"stdout={Truncate(stdout, 160)}");
        }

        return string.Join("，", parts);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string NewJobId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static void TryDeleteFile(string path)
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
            // 清理失败不影响自检结论。
        }
    }
}
