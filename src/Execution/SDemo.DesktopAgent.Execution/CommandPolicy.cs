using System.Text;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Execution;

/// <summary>命令策略结果。拒绝时 <see cref="Reason"/> 是给用户 / 模型看的中文原因。</summary>
public sealed record CommandPolicyResult(
    bool Allowed,
    string Reason,
    ExecutionMode Mode,
    string Command = "",
    IReadOnlyList<string>? Arguments = null,
    string WorkingDirectory = ".",
    string Prefix = "")
{
    public static CommandPolicyResult Deny(string reason, ExecutionMode mode) =>
        new(false, reason, mode);
}

/// <summary>
/// 直跑白名单与参数校验（P4 §3.3）。这里不启动进程，只做纯策略判断，便于单测。
/// 真正的执行仍由 Windows 宿主 <c>DirectProcessRunner</c> 完成。
/// </summary>
public static class CommandPolicy
{
    public const int MaxArgumentsChars = 4 * 1024;

    public const string ShellCharacterHint =
        "参数里不能包含 shell 元字符（& | > < ; $ ( ) ` ^ %）或换行。";

    private static readonly char[] ShellCharacters =
        ['&', '|', '>', '<', ';', '$', '(', ')', '`', '^', '%', '\r', '\n'];

    private static readonly HashSet<string> GitReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "rev-parse"
    };

    private static readonly HashSet<string> HighRiskTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "del", "erase", "format", "rd", "rmdir", "reset", "clean"
    };

    /// <summary>执行策略。工作目录不存在 / 越界、命令不在白名单、参数不合法都会拒绝。</summary>
    public static CommandPolicyResult Evaluate(
        string? command,
        string? args,
        string? workingDirectory,
        string workspaceRoot,
        ExecutionSettings settings)
    {
        settings ??= new ExecutionSettings();

        if (!settings.DirectRunEnabled || settings.Mode != ExecutionMode.AllowlistDirect)
        {
            return CommandPolicyResult.Deny(
                "直跑未开启（默认走沙箱）。要启用请在「设置 → 工具设置 → 内循环」打开。",
                settings.Mode);
        }

        var value = command?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            return CommandPolicyResult.Deny("命令为空。", settings.Mode);
        }

        if (!IsExecutableName(value))
        {
            return CommandPolicyResult.Deny(
                "command 只能是可执行文件名，不能带路径、扩展名或 shell（例如用 dotnet，而不是 C:\\...\\dotnet.exe 或 cmd /c）。",
                settings.Mode);
        }

        var allowlist = settings.Allowlist ?? [];
        if (!allowlist.Any(item => string.Equals(item?.Trim(), value, StringComparison.OrdinalIgnoreCase)))
        {
            return CommandPolicyResult.Deny(
                $"命令不在白名单：{value}。可在「设置 → 工具设置 → 内循环」调整，或改用 run_powershell 进沙箱。",
                settings.Mode);
        }

        var rawArgs = args?.Trim() ?? string.Empty;
        if (rawArgs.Length > MaxArgumentsChars)
        {
            return CommandPolicyResult.Deny($"参数超过 {MaxArgumentsChars} 字符上限。", settings.Mode);
        }

        if (rawArgs.IndexOfAny(ShellCharacters) >= 0)
        {
            return CommandPolicyResult.Deny(ShellCharacterHint, settings.Mode);
        }

        IReadOnlyList<string> arguments;
        try
        {
            arguments = SplitArguments(rawArgs);
        }
        catch (FormatException ex)
        {
            return CommandPolicyResult.Deny(ex.Message, settings.Mode);
        }

        foreach (var argument in arguments)
        {
            if (argument.IndexOfAny(['\r', '\n']) >= 0)
            {
                return CommandPolicyResult.Deny("参数不能包含换行。", settings.Mode);
            }

            if (ContainsTraversal(argument))
            {
                return CommandPolicyResult.Deny($"参数不能包含 .. 越界：{argument}", settings.Mode);
            }

            if (LooksLikeAbsolutePath(argument))
            {
                return CommandPolicyResult.Deny($"参数不能是绝对路径 / UNC：{argument}", settings.Mode);
            }
        }

        if (string.Equals(value, "git", StringComparison.OrdinalIgnoreCase))
        {
            var subcommand = arguments.FirstOrDefault();
            if (subcommand is null || !GitReadOnly.Contains(subcommand))
            {
                return CommandPolicyResult.Deny(
                    "git 只允许只读子命令：status / diff / log / show / rev-parse。",
                    settings.Mode);
            }
        }

        var directory = workingDirectory?.Trim();
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        string fullDirectory;
        try
        {
            fullDirectory = WorkspacePath.Resolve(workspaceRoot, directory, allowRoot: true);
        }
        catch (InvalidOperationException ex)
        {
            return CommandPolicyResult.Deny($"工作目录无效：{ex.Message}", settings.Mode);
        }

        if (!Directory.Exists(fullDirectory))
        {
            return CommandPolicyResult.Deny($"工作目录不存在：{directory}", settings.Mode);
        }

        var relative = WorkspacePath.Relative(workspaceRoot, fullDirectory);
        var prefix = BuildPrefix(value, arguments);

        return new CommandPolicyResult(
            true,
            string.Empty,
            ExecutionMode.AllowlistDirect,
            value,
            arguments,
            relative,
            prefix);
    }

    /// <summary>是否高风险命令；高风险命令永远不提供「本会话允许」。</summary>
    public static bool IsHighRisk(string command, IReadOnlyList<string> arguments)
    {
        if (string.Equals(command, "git", StringComparison.OrdinalIgnoreCase))
        {
            var subcommand = arguments.FirstOrDefault();
            if (subcommand is not null
                && (string.Equals(subcommand, "reset", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(subcommand, "clean", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (HighRiskTokens.Contains(command))
        {
            return true;
        }

        return arguments.Any(argument => HighRiskTokens.Contains(argument));
    }

    /// <summary>会话授权前缀：可执行文件 + 前两个参数。</summary>
    public static string BuildPrefix(string command, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { command }.Concat(arguments.Take(2))).ToLowerInvariant();

    /// <summary>按空白切分；引号内保留空格并去掉外层引号。不支持转义与 shell 展开。</summary>
    public static IReadOnlyList<string> SplitArguments(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var started = false;

        foreach (var character in args)
        {
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }

                started = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(character);
            started = true;
        }

        if (quote is not null)
        {
            throw new FormatException("参数里的引号没有闭合。");
        }

        if (started)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static bool IsExecutableName(string value) =>
        value.IndexOfAny(['/', '\\', ':', '"', '\'', '\0']) < 0
        && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !value.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        && !value.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        && !value.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsTraversal(string argument)
    {
        var normalized = argument.Replace('\\', '/');
        return normalized.Split('/').Any(segment => segment == "..");
    }

    private static bool LooksLikeAbsolutePath(string argument)
    {
        if (argument.StartsWith(@"\\", StringComparison.Ordinal)
            || argument.StartsWith("//", StringComparison.Ordinal)
            || argument.StartsWith("/", StringComparison.Ordinal)
            || argument.StartsWith(@"\", StringComparison.Ordinal))
        {
            return true;
        }

        return argument.Length >= 2
               && char.IsLetter(argument[0])
               && argument[1] == ':';
    }
}
