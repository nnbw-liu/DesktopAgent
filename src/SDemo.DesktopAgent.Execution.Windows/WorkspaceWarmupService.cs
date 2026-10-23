using System.Security.Cryptography;
using System.Text;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Execution;

/// <summary>
/// 预 restore 只跑固定的 <c>dotnet restore</c>，不接受模型参数；用户点击按钮即视为授权。
/// 它不开启直跑模式，也不改变 run_powershell 的沙箱边界。
/// </summary>
public sealed class WorkspaceWarmupService : IWorkspaceWarmupService
{
    private readonly IDirectProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly IToolAuditLog _audit;

    public WorkspaceWarmupService(
        IDirectProcessRunner runner,
        ISettingsService settings,
        IToolAuditLog audit)
    {
        _runner = runner;
        _settings = settings;
        _audit = audit;
    }

    public async Task<WorkspaceWarmupResult> RestoreAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            return new WorkspaceWarmupResult(false, -1, "工作区目录不存在。", 0);
        }

        var hasProject = Directory.EnumerateFiles(workspaceRoot, "*.sln", SearchOption.TopDirectoryOnly).Any()
                         || Directory.EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.TopDirectoryOnly).Any();

        if (!hasProject)
        {
            return new WorkspaceWarmupResult(false, -1, "根目录没有 .sln / .csproj，未执行 restore。", 0);
        }

        var dotnet = DirectProcessRunner.ResolveExecutable("dotnet");
        if (dotnet is null)
        {
            return new WorkspaceWarmupResult(false, -1, "找不到 dotnet。", 0);
        }

        var timeout = Math.Clamp(_settings.Current.Execution.BuildTimeoutSeconds, 30, 900);
        var started = DateTime.Now;

        try
        {
            var result = await _runner.RunAsync(
                dotnet,
                ["restore"],
                workspaceRoot,
                TimeSpan.FromSeconds(timeout),
                Math.Clamp(_settings.Current.Execution.MaxOutputChars, 512, 64_000),
                cancellationToken);

            var success = result.ExitCode == 0 && !result.TimedOut;
            var message = success
                ? $"restore 完成（{result.DurationMs / 1000.0:0.0}s）。"
                : result.TimedOut
                    ? "restore 超时。"
                    : $"restore 失败（退出码 {result.ExitCode}）：{Tail(result.StdErr + result.StdOut)}";

            _audit.Write(new ToolAuditEntry
            {
                SessionId = "workspace-warmup",
                ToolId = "workspace_restore",
                Sandbox = SandboxKind.None,
                Mode = "direct",
                CommandSha256 = "sha256:" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes("dotnet restore"))).ToLowerInvariant(),
                WorkingDirectory = ".",
                ExitCode = result.ExitCode,
                TimedOut = result.TimedOut,
                DurationMs = result.DurationMs,
                StdoutBytes = Encoding.UTF8.GetByteCount(result.StdOut),
                StderrBytes = Encoding.UTF8.GetByteCount(result.StdErr),
                Approval = "UserInitiated",
                Error = success ? null : message
            });

            return new WorkspaceWarmupResult(success, result.ExitCode, message, result.DurationMs);
        }
        catch (OperationCanceledException)
        {
            return new WorkspaceWarmupResult(false, -1, "restore 已取消。", (long)(DateTime.Now - started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return new WorkspaceWarmupResult(false, -1, $"restore 失败：{ex.Message}", 0);
        }
    }

    private static string Tail(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.TakeLast(3)).Trim();
    }
}
