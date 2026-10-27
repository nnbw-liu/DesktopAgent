using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Tools;

/// <summary>
/// PowerShell 工具包（S3）：脚本写进工作区副本，在 AppContainer 中执行；真实工作区只在
/// 用户于变更面板确认后由 <c>ChangeApplier</c> 写回。每次执行都必须经过通用审批，
/// 不提供“同脚本自动批准”，避免一次授权后被替换 / 重放。
/// </summary>
public sealed class PowerShellToolPack : IAgentToolPack
{
    public const string PackId = "powershell";
    public const string FunctionId = "run_powershell";

    /// <summary>脚本上限 64KB：超过就直接拒绝，避免审批框无法展示完整脚本。</summary>
    public const int MaxScriptBytes = 64 * 1024;

    private readonly IDirectProcessRunner _runner;

    public PowerShellToolPack(IDirectProcessRunner runner) => _runner = runner;

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "PowerShell",
        Description:
            "在沙箱中运行 Windows PowerShell 5.1 脚本（无网络、不能访问真实工作区），"
            + "脚本对文件副本的改动需要用户确认后才会写回。",
        Glyph: ">_",
        Risk: AgentToolRisk.High,
        Order: 30,
        DefaultEnabled: true,
        WindowsOnly: true,
        Functions:
        [
            new AgentToolFunctionDescriptor(
                FunctionId,
                "运行 PowerShell",
                AgentToolRisk.High,
                RequiresApproval: true)
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        // 每次 Run 创建一个绑定 context 的函数对象，避免单例 Pack 持有上一个会话的上下文。
        var functions = new PowerShellFunctions(context, this);

        // 聊天室关掉沙箱时模型看到的是直跑语义，工具描述必须跟着变，否则它会以为还要等写回确认。
        var sandboxed = context.Scope?.Sandbox.Enabled ?? context.Settings.Current.Sandbox.Enabled;

        return
        [
            AIFunctionFactory.Create(
                functions.RunPowerShellAsync,
                name: FunctionId,
                description: sandboxed
                    ? "在工作区副本中运行 Windows PowerShell 5.1 脚本；改动需要用户确认后才会写回真实工作区。"
                      + "沙箱无网络、不能读取用户文件；AppContainer 模式下脚本运行在 ConstrainedLanguage，"
                      + "不能依赖 .NET 静态方法或 Add-Type。"
                    : "直接在工作区运行 Windows PowerShell 5.1 脚本（这个聊天室已关闭沙箱）："
                      + "改动立即落到真实文件，可以联网、可以访问用户目录。执行前请确认脚本是对的。")
        ];
    }

    /// <summary>
    /// 工具入口。公开是为了让集成测试直接走同一条校验 / 审批 / 审计路径（与 AIFunction 调用完全相同）。
    /// </summary>
    public async Task<string> RunPowerShellAsync(
        ToolContext context,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Scope?.Sandbox ?? context.Settings.Current.Sandbox;

        // 沙箱开关按聊天室解析：关掉沙箱不是「禁用工具」，而是改成宿主直跑（见 RunDirectAsync）。
        var sandboxed = settings.Enabled;

        if (!context.Settings.Current.Tools.IsPackEnabled(PackId, Descriptor.DefaultEnabled))
        {
            return "ERROR: PowerShell 工具未启用。请在「设置 → 工具设置」中打开 PowerShell。";
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return "ERROR: 脚本为空。";
        }

        if (Encoding.UTF8.GetByteCount(script) > MaxScriptBytes)
        {
            return $"ERROR: 脚本超过 {MaxScriptBytes / 1024}KB 上限，未执行。";
        }

        SandboxAvailability? availability = null;

        if (sandboxed)
        {
            availability = await context.SandboxJobs.GetAvailabilityAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!availability.Available || !availability.Verified)
            {
                return "ERROR: 沙箱不可用，PowerShell 已禁用。"
                       + (string.IsNullOrWhiteSpace(availability.Reason)
                           ? string.Empty
                           : $" {availability.Reason}");
            }
        }

        var workingError = ResolveWorkingDirectory(
            context.WorkspaceRoot,
            workingDirectory,
            out var relativeDirectory,
            out var displayDirectory);

        if (workingError is not null)
        {
            return $"ERROR: 工作目录无效：{workingError}，未执行。";
        }

        var timeout = Math.Clamp(
            timeoutSeconds <= 0 ? settings.TimeoutSeconds : timeoutSeconds,
            1,
            120);
        var scriptHash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))
            .ToLowerInvariant();

        // 关掉沙箱时没有 availability（不检测沙箱），审计里记 None。
        var sandboxKind = availability?.Kind ?? SandboxKind.None;

        // 审批框展示的脚本、工作目录、超时必须就是实际执行的那一份。
        var decision = await context.Approvals.RequestAsync(
            new ToolApprovalRequest(
                ToolId: FunctionId,
                Kind: ToolApprovalKind.ProcessExecution,
                Title: sandboxed ? "运行 PowerShell 脚本" : "直跑 PowerShell 脚本（无沙箱）",
                Summary: sandboxed
                    ? $"沙箱：{SandboxLabel(sandboxKind)}；目录：{displayDirectory}；超时：{timeout}s；无网络，只能访问工作区副本"
                    : $"直跑（无沙箱）：目录：{displayDirectory}；超时：{timeout}s；直接修改真实工作区，不隔离网络",
                Detail: script,
                Risk: AgentToolRisk.High,
                AllowSessionRemember: false,
                SessionKey: null),
            cancellationToken).ConfigureAwait(false);

        if (decision == ToolApprovalDecision.Deny)
        {
            WriteAudit(
                context,
                settings,
                script,
                new ToolAuditEntry
                {
                    SessionId = context.SessionId,
                    ToolId = FunctionId,
                    Sandbox = sandboxKind,
                    ScriptSha256 = scriptHash,
                    Approval = "Deny",
                    Error = "用户拒绝了这次执行。"
                });

            return "用户拒绝了这次 PowerShell 执行，未做任何修改。";
        }

        // 聊天室关掉沙箱：同一份脚本改在宿主直跑，改动立即落到真实工作区。
        if (!sandboxed)
        {
            return await RunDirectAsync(
                    context,
                    settings,
                    script,
                    scriptHash,
                    relativeDirectory,
                    displayDirectory,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        SandboxJobOutcome outcome;

        try
        {
            outcome = await context.SandboxJobs.RunPowerShellAsync(
                    context.SessionId,
                    script,
                    relativeDirectory,
                    timeout,
                    workspaceRoot: context.WorkspaceRoot,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WriteAudit(
                context,
                settings,
                script,
                new ToolAuditEntry
                {
                    SessionId = context.SessionId,
                    ToolId = FunctionId,
                    Sandbox = sandboxKind,
                    ScriptSha256 = scriptHash,
                    Approval = "ApproveOnce",
                    Error = "执行被取消。"
                });

            return "已取消；沙箱进程树已终止。";
        }
        catch (Exception ex)
        {
            WriteAudit(
                context,
                settings,
                script,
                new ToolAuditEntry
                {
                    SessionId = context.SessionId,
                    ToolId = FunctionId,
                    Sandbox = sandboxKind,
                    ScriptSha256 = scriptHash,
                    Approval = "ApproveOnce",
                    Error = ex.Message
                });

            return $"ERROR: 沙箱执行失败：{ex.Message}";
        }

        var result = outcome.Result;

        WriteAudit(
            context,
            settings,
            script,
            new ToolAuditEntry
            {
                SessionId = context.SessionId,
                ToolId = FunctionId,
                JobId = outcome.JobId,
                Sandbox = result.Kind,
                ScriptSha256 = scriptHash,
                ExitCode = result.Failure == SandboxFailureCode.None ? result.ExitCode : null,
                TimedOut = result.TimedOut,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                StdoutBytes = Encoding.UTF8.GetByteCount(result.StdOut),
                StderrBytes = Encoding.UTF8.GetByteCount(result.StdErr),
                ChangeCount = outcome.Changes.Count,
                Changes = outcome.Changes
                    .Select(change => new ToolAuditChange(
                        change.Kind.ToString(),
                        "sha256:" + Convert.ToHexString(
                                SHA256.HashData(Encoding.UTF8.GetBytes(change.RelativePath)))
                            .ToLowerInvariant()))
                    .ToArray(),
                Approval = "ApproveOnce",
                Error = result.Message
            });

        return PowerShellResultFormatter.Format(outcome);
    }

    /// <summary>
    /// 关闭沙箱后的直跑路径：在真实工作区执行同一份脚本，没有副本、没有写回确认。
    /// 审批仍由 <c>ToolContext.Approvals</c> 决定（聊天室打开「完全权限」时它已自动批准）。
    /// </summary>
    private async Task<string> RunDirectAsync(
        ToolContext context,
        SandboxSettings settings,
        string script,
        string scriptHash,
        string? relativeDirectory,
        string displayDirectory,
        int timeout,
        CancellationToken cancellationToken)
    {
        var executable = DirectProcessRunner.ResolveExecutable("powershell")
                         ?? Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.System),
                             "WindowsPowerShell",
                             "v1.0",
                             "powershell.exe");

        if (!File.Exists(executable))
        {
            return "ERROR: 找不到 powershell.exe，未执行。";
        }

        var directory = context.WorkspaceRoot;

        if (!string.IsNullOrWhiteSpace(relativeDirectory))
        {
            directory = Path.Combine(
                context.WorkspaceRoot,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        }

        var execution = context.Scope?.Execution ?? context.Settings.Current.Execution;
        var maxOutputChars = Math.Clamp(execution.MaxOutputChars, 512, 64_000);

        DirectRunResult result;

        try
        {
            result = await _runner.RunAsync(
                    executable,
                    [
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-ExecutionPolicy", "Bypass",
                        // 脚本按 UTF-16LE 编码传给 PowerShell，避免引号 / 换行被命令行解析吃掉。
                        "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
                    ],
                    directory,
                    TimeSpan.FromSeconds(timeout),
                    maxOutputChars,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WriteAudit(
                context,
                settings,
                script,
                new ToolAuditEntry
                {
                    SessionId = context.SessionId,
                    ToolId = FunctionId,
                    Sandbox = SandboxKind.None,
                    ScriptSha256 = scriptHash,
                    Approval = "ApproveOnce",
                    Mode = "direct",
                    WorkingDirectory = displayDirectory,
                    Error = "执行被取消。"
                });

            return "已取消；直跑进程树已终止。";
        }
        catch (Exception ex)
        {
            WriteAudit(
                context,
                settings,
                script,
                new ToolAuditEntry
                {
                    SessionId = context.SessionId,
                    ToolId = FunctionId,
                    Sandbox = SandboxKind.None,
                    ScriptSha256 = scriptHash,
                    Approval = "ApproveOnce",
                    Mode = "direct",
                    WorkingDirectory = displayDirectory,
                    Error = ex.Message
                });

            return $"ERROR: 直跑失败：{ex.Message}";
        }

        WriteAudit(
            context,
            settings,
            script,
            new ToolAuditEntry
            {
                SessionId = context.SessionId,
                ToolId = FunctionId,
                Sandbox = SandboxKind.None,
                ScriptSha256 = scriptHash,
                ExitCode = result.Canceled ? null : result.ExitCode,
                TimedOut = result.TimedOut,
                DurationMs = result.DurationMs,
                StdoutBytes = Encoding.UTF8.GetByteCount(result.StdOut),
                StderrBytes = Encoding.UTF8.GetByteCount(result.StdErr),
                Approval = "ApproveOnce",
                Mode = "direct",
                WorkingDirectory = displayDirectory,
                Error = result.TimedOut ? "执行超时。" : null
            });

        return FormatDirectResult(displayDirectory, result);
    }

    /// <summary>直跑结果：没有变更集，输出直接就是真实工作区的结果。</summary>
    private static string FormatDirectResult(string displayDirectory, DirectRunResult result)
    {
        var builder = new StringBuilder();

        builder.Append("直跑（无沙箱）：").AppendLine(displayDirectory);
        builder.Append("退出码：").AppendLine(result.ExitCode.ToString());
        builder.Append("耗时：").Append(result.DurationMs.ToString("0")).AppendLine(" ms");

        if (result.TimedOut)
        {
            builder.AppendLine("已超时并终止进程树；以下是截止终止前的输出。");
        }

        if (result.Canceled)
        {
            builder.AppendLine("用户取消了这次执行；以下是截止取消前的输出。");
        }

        AppendDirectStream(builder, "stdout", result.StdOut);
        AppendDirectStream(builder, "stderr", result.StdErr);

        builder.AppendLine();
        builder.AppendLine("改动已经直接写入真实工作区，不需要在面板里确认。");

        return builder.ToString().TrimEnd();
    }

    private static void AppendDirectStream(StringBuilder builder, string name, string text)
    {
        builder.AppendLine();
        builder.Append("--- ").Append(name).AppendLine(" ---");

        if (string.IsNullOrEmpty(text))
        {
            builder.AppendLine("（空）");
            return;
        }

        if (text.Length > PowerShellResultFormatter.MaxOutputCharsPerStream)
        {
            builder.AppendLine(text[..PowerShellResultFormatter.MaxOutputCharsPerStream]);
            builder.AppendLine("…（输出已截断）");
            return;
        }

        builder.AppendLine(text.TrimEnd());
    }

    /// <summary>审批框里的沙箱标签：Windows Sandbox 需要让用户预期到 VM 启动时间。</summary>
    private static string SandboxLabel(SandboxKind kind) => kind switch
    {
        SandboxKind.WindowsSandbox => "Windows Sandbox（VM，无网络，启动约 10–30 秒）",
        SandboxKind.AppContainer => "AppContainer",
        _ => kind.ToString()
    };

    private static void WriteAudit(
        ToolContext context,
        SandboxSettings settings,
        string script,
        ToolAuditEntry entry)
    {
        context.Audit.Write(entry with
        {
            // 默认只留 hash；只有用户显式打开“保留脚本正文”时才写前 4KB。
            ScriptPreview = settings.RetainScriptsInAuditLog ? script : null
        });
    }

    /// <summary>
    /// 解析相对工作目录：空 / <c>.</c> 表示工作区根目录；只接受工作区内的相对路径。
    /// 审批框显示的就是解析后的相对路径。
    /// </summary>
    private static string? ResolveWorkingDirectory(
        string workspaceRoot,
        string? workingDirectory,
        out string? relativeDirectory,
        out string displayDirectory)
    {
        relativeDirectory = null;
        displayDirectory = ".";

        var value = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory.Trim();

        if (value.Contains('\0')
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            return $"必须是工作区内的相对路径：{workingDirectory}";
        }

        var segments = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".."))
        {
            return $"不能包含 ..：{workingDirectory}";
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var full = Path.GetFullPath(
            Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));

        if (!SandboxPathGuard.IsUnder(root, full))
        {
            return $"超出工作区范围：{workingDirectory}";
        }

        if (!Directory.Exists(full))
        {
            return $"目录不存在：{workingDirectory}";
        }

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return $"包含符号链接或 junction：{workingDirectory}";
            }
        }

        if (segments.Length > 0)
        {
            relativeDirectory = string.Join('/', segments);
            displayDirectory = "./" + relativeDirectory;
        }

        return null;
    }

    private sealed class PowerShellFunctions(ToolContext context, PowerShellToolPack pack)
    {
        public Task<string> RunPowerShellAsync(
            [Description("要执行的 PowerShell 5.1 脚本，最多 64KB。")]
            string script,
            [Description("工作目录，相对工作区；默认工作区根目录。")]
            string? workingDirectory = null,
            [Description("超时秒数，1–120；省略或传 0 时使用设置里的默认值。")]
            int timeoutSeconds = 0,
            CancellationToken cancellationToken = default) =>
            pack.RunPowerShellAsync(context, script, workingDirectory, timeoutSeconds, cancellationToken);
    }
}
