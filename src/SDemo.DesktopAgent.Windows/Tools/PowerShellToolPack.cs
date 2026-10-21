using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
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

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "PowerShell",
        Description:
            "在沙箱中运行 Windows PowerShell 5.1 脚本（无网络、不能访问真实工作区），"
            + "脚本对文件副本的改动需要用户确认后才会写回。",
        Glyph: ">_",
        Risk: AgentToolRisk.High,
        Order: 30,
        DefaultEnabled: false,
        WindowsOnly: true,
        Functions:
        [
            new AgentToolFunctionDescriptor(
                FunctionId,
                "运行 PowerShell",
                AgentToolRisk.High,
                RequiresApproval: true)
        ]);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        // 每次 Run 创建一个绑定 context 的函数对象，避免单例 Pack 持有上一个会话的上下文。
        var functions = new PowerShellFunctions(context, this);

        return
        [
            AIFunctionFactory.Create(
                functions.RunPowerShellAsync,
                name: FunctionId,
                description:
                    "在工作区副本中运行 Windows PowerShell 5.1 脚本；改动需要用户确认后才会写回真实工作区。"
                    + "沙箱无网络、不能读取用户文件；AppContainer 模式下脚本运行在 ConstrainedLanguage，"
                    + "不能依赖 .NET 静态方法或 Add-Type。")
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
        var settings = context.Settings.Current.Sandbox;

        if (!settings.Enabled)
        {
            return "ERROR: 沙箱不可用，PowerShell 已禁用。（执行沙箱已在设置中关闭）";
        }

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

        var availability = await context.SandboxJobs.GetAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!availability.Available || !availability.Verified)
        {
            return "ERROR: 沙箱不可用，PowerShell 已禁用。"
                   + (string.IsNullOrWhiteSpace(availability.Reason)
                       ? string.Empty
                       : $" {availability.Reason}");
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

        // 审批框展示的脚本、工作目录、超时必须就是实际执行的那一份。
        var decision = await context.Approvals.RequestAsync(
            new ToolApprovalRequest(
                ToolId: FunctionId,
                Kind: ToolApprovalKind.ProcessExecution,
                Title: "运行 PowerShell 脚本",
                Summary:
                    $"沙箱：{SandboxLabel(availability.Kind)}；目录：{displayDirectory}；超时：{timeout}s；无网络，只能访问工作区副本",
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
                    Sandbox = availability.Kind,
                    ScriptSha256 = scriptHash,
                    Approval = "Deny",
                    Error = "用户拒绝了这次执行。"
                });

            return "用户拒绝了这次 PowerShell 执行，未做任何修改。";
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
                    Sandbox = availability.Kind,
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
                    Sandbox = availability.Kind,
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
                Approval = "ApproveOnce",
                Error = result.Message
            });

        return PowerShellResultFormatter.Format(outcome);
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
