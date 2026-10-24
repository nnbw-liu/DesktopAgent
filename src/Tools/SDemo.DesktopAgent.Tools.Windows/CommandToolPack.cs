using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Tools;

/// <summary>
/// P4 直跑命令工具。只放行白名单里的可执行文件，参数不经过 shell；每次执行写审计，
/// 安全命令可以申请本会话授权，高风险命令永远逐次审批。直跑默认关闭。
/// </summary>
public sealed class CommandToolPack : IAgentToolPack
{
    public const string PackId = "command";
    public const string FunctionId = "run_command";

    private readonly IDirectProcessRunner _runner;
    private readonly SessionGrantStore _grants;

    public CommandToolPack(IDirectProcessRunner runner, SessionGrantStore grants)
    {
        _runner = runner;
        _grants = grants;
    }

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "命令（直跑）",
        Description:
            "在宿主直接运行白名单命令（dotnet / npm / pnpm / node / git 只读子命令等），"
            + "用于构建与测试；参数不经过 shell，每次执行写审计。默认关闭。",
        Glyph: ">_",
        Risk: AgentToolRisk.Medium,
        Order: 35,
        DefaultEnabled: false,
        WindowsOnly: true,
        Functions:
        [
            new AgentToolFunctionDescriptor(
                FunctionId,
                "运行命令",
                AgentToolRisk.Medium,
                RequiresApproval: true)
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new CommandFunctions(context, this);

        return
        [
            AIFunctionFactory.Create(
                functions.RunCommandAsync,
                name: FunctionId,
                description:
                    "在宿主直跑白名单命令（如 dotnet build / dotnet test），用于构建和测试。"
                    + "命令不能带路径或 shell 元字符；直跑需要先在设置里开启，并会请求审批。"
                    + "需要网络或真实工作区时使用它；沙箱脚本仍用 run_powershell。")
        ];
    }

    /// <summary>公开给集成测试走同一条校验 / 审批 / 审计路径。</summary>
    public async Task<string> RunCommandAsync(
        ToolContext context,
        string command,
        string? args,
        string? workingDirectory,
        int timeoutSeconds,
        string? purpose,
        CancellationToken cancellationToken = default)
    {
        var execution = context.Scope?.Execution ?? context.Settings.Current.Execution;

        if (!context.Settings.Current.Tools.IsPackEnabled(PackId, Descriptor.DefaultEnabled))
        {
            return "ERROR: 直跑命令工具未启用。请在「设置 → 工具设置 → 内循环」打开。";
        }

        var policy = CommandPolicy.Evaluate(
            command,
            args,
            workingDirectory,
            context.WorkspaceRoot,
            execution);

        if (!policy.Allowed)
        {
            return $"ERROR: {policy.Reason}";
        }

        var executable = DirectProcessRunner.ResolveExecutable(policy.Command);
        if (executable is null)
        {
            return $"ERROR: 找不到可执行文件：{policy.Command}，未执行。";
        }

        var arguments = policy.Arguments ?? [];
        var commandLine = string.Join(' ', new[] { policy.Command }.Concat(arguments));
        var commandHash = "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(commandLine)))
            .ToLowerInvariant();

        var timeout = ResolveTimeout(timeoutSeconds, execution, command, arguments);
        var highRisk = CommandPolicy.IsHighRisk(policy.Command, arguments);
        var fullDirectory = Path.Combine(
            context.WorkspaceRoot,
            policy.WorkingDirectory.Replace('/', Path.DirectorySeparatorChar));

        var approval = "NotRequired";

        if (!_grants.IsGranted(context.SessionId, policy.Prefix))
        {
            var decision = await context.Approvals.RequestAsync(
                new ToolApprovalRequest(
                    ToolId: FunctionId,
                    Kind: ToolApprovalKind.ProcessExecution,
                    Title: $"直跑命令：{policy.Command}",
                    Summary:
                        $"直跑不受沙箱网络隔离；目录：{DisplayDirectory(policy.WorkingDirectory)}；"
                        + $"超时：{timeout}s；命令：{Truncate(commandLine, 200)}",
                    Detail: commandLine,
                    Risk: highRisk ? AgentToolRisk.High : AgentToolRisk.Medium,
                    AllowSessionRemember: !highRisk,
                    SessionKey: policy.Prefix),
                cancellationToken).ConfigureAwait(false);

            if (decision == ToolApprovalDecision.Deny)
            {
                WriteAudit(context, commandHash, policy, approval: "Deny", error: "用户拒绝了这次执行。");
                return "用户拒绝了这次直跑命令，未执行。";
            }

            if (decision == ToolApprovalDecision.ApproveForSession && !highRisk)
            {
                _grants.Grant(context.SessionId, policy.Prefix, policy.Command, highRisk: false);
                approval = "SessionGrant";
            }
            else
            {
                approval = "ApproveOnce";
            }
        }
        else
        {
            approval = "SessionGrant";
        }

        DirectRunResult result;

        try
        {
            result = await _runner.RunAsync(
                    executable,
                    arguments,
                    fullDirectory,
                    TimeSpan.FromSeconds(timeout),
                    Math.Clamp(execution.MaxOutputChars, 512, 64_000),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WriteAudit(context, commandHash, policy, approval, error: "执行被取消。");
            return "已取消；直跑进程树已终止。";
        }
        catch (Exception ex)
        {
            WriteAudit(context, commandHash, policy, approval, error: ex.Message);
            return $"ERROR: 直跑失败：{ex.Message}";
        }

        WriteAudit(
            context,
            commandHash,
            policy,
            approval,
            exitCode: result.ExitCode,
            timedOut: result.TimedOut,
            durationMs: result.DurationMs,
            stdoutBytes: Encoding.UTF8.GetByteCount(result.StdOut),
            stderrBytes: Encoding.UTF8.GetByteCount(result.StdErr),
            error: result.TimedOut ? "执行超时。" : null);

        RecordVerification(context, commandLine, result, purpose);

        return FormatResult(commandLine, result);
    }

    private static int ResolveTimeout(
        int requested,
        ExecutionSettings execution,
        string command,
        IReadOnlyList<string> arguments)
    {
        var fallback = IsBuildOrTest(command, arguments)
            ? execution.BuildTimeoutSeconds
            : execution.DefaultTimeoutSeconds;

        return Math.Clamp(requested <= 0 ? fallback : requested, 1, Math.Max(1, execution.BuildTimeoutSeconds));
    }

    private static void RecordVerification(
        ToolContext context,
        string commandLine,
        DirectRunResult result,
        string? purpose)
    {
        if (context.Session is null)
        {
            return;
        }

        var text = string.Join('\n', result.StdOut, result.StdErr);
        var lowerPurpose = purpose?.ToLowerInvariant() ?? string.Empty;

        if (lowerPurpose.Contains("test", StringComparison.Ordinal)
            || lowerPurpose.Contains("测试", StringComparison.Ordinal)
            || IsTestCommand(commandLine))
        {
            context.Session.LastVerification = TestResultParser.Parse(
                commandLine,
                result.StdOut,
                result.StdErr,
                result.ExitCode,
                result.DurationMs);
            return;
        }

        if (lowerPurpose.Contains("build", StringComparison.Ordinal)
            || lowerPurpose.Contains("构建", StringComparison.Ordinal)
            || IsBuildCommand(commandLine)
            || text.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("生成成功", StringComparison.Ordinal))
        {
            context.Session.LastVerification = BuildResultParser.Parse(
                commandLine,
                result.StdOut,
                result.StdErr,
                result.ExitCode,
                result.DurationMs);
        }
    }

    private static bool IsBuildOrTest(string command, IReadOnlyList<string> arguments) =>
        (string.Equals(command, "dotnet", StringComparison.OrdinalIgnoreCase)
         && arguments.Any(argument => argument is "build" or "test" or "restore"))
        || arguments.Any(argument => argument is "test");

    private static bool IsBuildCommand(string commandLine) =>
        commandLine.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains(" dotnet build", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains(" msbuild", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestCommand(string commandLine) =>
        commandLine.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains(" dotnet test", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains("npm test", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains("pnpm test", StringComparison.OrdinalIgnoreCase);

    private static string FormatResult(string commandLine, DirectRunResult result)
    {
        var builder = new StringBuilder();
        builder.Append("命令：").AppendLine(commandLine);
        builder.Append("模式：直跑（不受沙箱网络隔离）；耗时：").Append(result.DurationMs).AppendLine("ms");
        builder.Append("退出码：").Append(result.ExitCode)
            .Append(result.TimedOut ? "（超时）" : string.Empty).AppendLine();

        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            builder.AppendLine("--- stdout ---").AppendLine(result.StdOut.TrimEnd());
            if (result.StdOutTruncated)
            {
                builder.AppendLine("…（stdout 已截断）");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            builder.AppendLine("--- stderr ---").AppendLine(result.StdErr.TrimEnd());
            if (result.StdErrTruncated)
            {
                builder.AppendLine("…（stderr 已截断）");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string DisplayDirectory(string relative) =>
        string.IsNullOrEmpty(relative) || relative == "." ? "." : "./" + relative;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static void WriteAudit(
        ToolContext context,
        string commandHash,
        CommandPolicyResult policy,
        string approval,
        int? exitCode = null,
        bool timedOut = false,
        long durationMs = 0,
        int stdoutBytes = 0,
        int stderrBytes = 0,
        string? error = null)
    {
        context.Audit.Write(new ToolAuditEntry
        {
            SessionId = context.SessionId,
            ToolId = FunctionId,
            Sandbox = SandboxKind.None,
            Mode = "direct",
            CommandSha256 = commandHash,
            WorkingDirectory = policy.WorkingDirectory,
            ExitCode = exitCode,
            TimedOut = timedOut,
            DurationMs = durationMs,
            StdoutBytes = stdoutBytes,
            StderrBytes = stderrBytes,
            Approval = approval,
            Error = error
        });
    }

    private sealed class CommandFunctions(ToolContext context, CommandToolPack pack)
    {
        public Task<string> RunCommandAsync(
            [Description("可执行文件名，例如 dotnet；不能带路径。")] string command,
            [Description("参数，例如 build Demo.sln -v minimal；不能包含 shell 元字符。")] string? args = null,
            [Description("工作目录，相对工作区；默认工作区根目录。")] string? workingDirectory = null,
            [Description("超时秒数；0 表示使用设置里的构建 / 默认超时。")] int timeoutSeconds = 0,
            [Description("用途说明（构建 / 测试 / 其他），只用于展示与审计。")] string? purpose = null,
            CancellationToken cancellationToken = default) =>
            pack.RunCommandAsync(
                context,
                command,
                args,
                workingDirectory,
                timeoutSeconds,
                purpose,
                cancellationToken);
    }
}
