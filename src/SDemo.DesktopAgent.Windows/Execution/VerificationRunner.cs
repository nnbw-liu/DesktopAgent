using System.Security.Cryptography;
using System.Text;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Execution;

/// <summary>
/// 用户点「运行验证」时执行工作区已保存的 build / test 命令。
/// 命令来自设置（用户确认过），不接受模型参数；仍写审计，审批来源固定为 UserInitiated。
/// </summary>
public sealed class VerificationRunner : IVerificationRunner
{
    private readonly IDirectProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly IToolAuditLog _audit;

    public VerificationRunner(
        IDirectProcessRunner runner,
        ISettingsService settings,
        IToolAuditLog audit)
    {
        _runner = runner;
        _settings = settings;
        _audit = audit;
    }

    public async Task<VerificationRecord?> RunAsync(
        ChatSession session,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var workspace = FindWorkspace(workspaceRoot);
        var suggestion = WorkspaceCommandDetector.Detect(workspaceRoot);
        var build = workspace?.BuildCommand ?? suggestion.BuildCommand;
        var test = workspace?.TestCommand ?? suggestion.TestCommand;

        if (string.IsNullOrWhiteSpace(build) && string.IsNullOrWhiteSpace(test))
        {
            return null;
        }

        VerificationRecord? last = null;

        if (!string.IsNullOrWhiteSpace(build))
        {
            last = await ExecuteAsync(build, "build", workspaceRoot, cancellationToken);
            session.LastVerification = last;

            if (last is { Success: false })
            {
                return last;
            }
        }

        if (!string.IsNullOrWhiteSpace(test))
        {
            last = await ExecuteAsync(test, "test", workspaceRoot, cancellationToken);
            session.LastVerification = last;
        }

        return last;
    }

    private async Task<VerificationRecord> ExecuteAsync(
        string commandLine,
        string kind,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var parts = CommandPolicy.SplitArguments(commandLine);
        if (parts.Count == 0)
        {
            return Failed(commandLine, kind, "命令为空。");
        }

        var executable = DirectProcessRunner.ResolveExecutable(parts[0]);
        if (executable is null)
        {
            return Failed(commandLine, kind, $"找不到可执行文件：{parts[0]}");
        }

        var timeout = Math.Clamp(_settings.Current.Execution.BuildTimeoutSeconds, 30, 900);
        var result = await _runner.RunAsync(
            executable,
            parts.Skip(1).ToArray(),
            workspaceRoot,
            TimeSpan.FromSeconds(timeout),
            Math.Clamp(_settings.Current.Execution.MaxOutputChars, 512, 64_000),
            cancellationToken);

        var record = kind == VerificationRecord.TestKind
            ? TestResultParser.Parse(commandLine, result.StdOut, result.StdErr, result.ExitCode, result.DurationMs)
            : BuildResultParser.Parse(commandLine, result.StdOut, result.StdErr, result.ExitCode, result.DurationMs);

        if (!record.Success && record.TopIssues.Count == 0)
        {
            var fallback = string.Join('\n', result.StdErr, result.StdOut)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(5)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

            record = record with { TopIssues = fallback };
        }

        _audit.Write(new ToolAuditEntry
        {
            SessionId = "verification",
            ToolId = "verification." + kind,
            Sandbox = SandboxKind.None,
            Mode = "direct",
            CommandSha256 = "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(commandLine))).ToLowerInvariant(),
            WorkingDirectory = ".",
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            DurationMs = result.DurationMs,
            StdoutBytes = Encoding.UTF8.GetByteCount(result.StdOut),
            StderrBytes = Encoding.UTF8.GetByteCount(result.StdErr),
            Approval = "UserInitiated",
            Error = record.Success ? null : record.TopIssues.FirstOrDefault()
        });

        return record;
    }

    private static VerificationRecord Failed(string command, string kind, string message) =>
        new(
            kind,
            command,
            false,
            -1,
            kind == VerificationRecord.BuildKind ? 1 : 0,
            0,
            0,
            0,
            [message],
            DateTime.Now,
            0);

    private Workspace? FindWorkspace(string root)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return _settings.Current.Workspaces.FirstOrDefault(workspace =>
            {
                try
                {
                    return string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path)),
                        fullRoot,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                    return false;
                }
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
