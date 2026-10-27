using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Execution;

/// <summary>技能脚本策略结果；拒绝时 <see cref="Reason"/> 是给模型 / 用户看的中文原因。</summary>
public sealed record SkillScriptPolicyResult(
    bool Allowed,
    string Reason,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int TimeoutSeconds)
{
    public static SkillScriptPolicyResult Deny(string reason) =>
        new(false, reason, [], ".", 0);
}

/// <summary>
/// 技能脚本的运行策略（P2）：直跑开关、Node 白名单、argv 校验与工作目录 jail。
/// 这里不解析脚本文件（由 <c>ISkillLibrary.ResolveScript</c> 负责），也不启动进程。
/// </summary>
public static class SkillScriptPolicy
{
    public const int MaxArguments = 32;

    public const int MaxArgumentChars = 2048;

    public const int MaxTotalChars = 4096;

    /// <summary>
    /// 校验一次技能脚本运行。参数直接作为 argv 传给 Node，不经过 shell，
    /// 因此这里只限制长度、控制字符与路径越界，不做 shell 元字符转义。
    /// </summary>
    public static SkillScriptPolicyResult Evaluate(
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        int requestedTimeoutSeconds,
        string workspaceRoot,
        ExecutionSettings settings)
    {
        settings ??= new ExecutionSettings();

        if (!settings.DirectRunEnabled || settings.Mode != ExecutionMode.AllowlistDirect)
        {
            return SkillScriptPolicyResult.Deny(
                "技能脚本需要直跑：请在「设置 → 工具设置 → 内循环」打开直跑命令。");
        }

        var allowlist = settings.Allowlist ?? [];
        if (!allowlist.Any(item => string.Equals(item?.Trim(), "node", StringComparison.OrdinalIgnoreCase)))
        {
            return SkillScriptPolicyResult.Deny(
                "直跑白名单里没有 node；请把 node 加回「设置 → 工具设置 → 内循环」的白名单。");
        }

        var values = (arguments ?? []).ToArray();

        if (values.Length > MaxArguments)
        {
            return SkillScriptPolicyResult.Deny($"参数最多 {MaxArguments} 个。");
        }

        var total = 0;

        foreach (var argument in values)
        {
            if (argument is null || argument.Length == 0)
            {
                return SkillScriptPolicyResult.Deny("参数不能为空字符串。");
            }

            if (argument.Length > MaxArgumentChars)
            {
                return SkillScriptPolicyResult.Deny($"单个参数不能超过 {MaxArgumentChars} 字符。");
            }

            total += argument.Length;
            if (total > MaxTotalChars)
            {
                return SkillScriptPolicyResult.Deny($"参数总长度不能超过 {MaxTotalChars} 字符。");
            }

            if (argument.Any(char.IsControl))
            {
                return SkillScriptPolicyResult.Deny($"参数不能包含控制字符或换行：{argument}");
            }

            if (ContainsTraversal(argument))
            {
                return SkillScriptPolicyResult.Deny($"参数不能包含 .. 越界：{argument}");
            }

            if (LooksLikeAbsolutePath(argument))
            {
                return SkillScriptPolicyResult.Deny(
                    $"参数不能是绝对路径 / UNC / 盘符路径：{argument}；请用相对工作区的路径。");
            }
        }

        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory.Trim();

        string fullDirectory;
        try
        {
            fullDirectory = WorkspacePath.Resolve(workspaceRoot, directory, allowRoot: true);
        }
        catch (InvalidOperationException ex)
        {
            return SkillScriptPolicyResult.Deny($"工作目录无效：{ex.Message}");
        }

        if (!Directory.Exists(fullDirectory))
        {
            return SkillScriptPolicyResult.Deny($"工作目录不存在：{directory}");
        }

        var fallback = settings.DefaultTimeoutSeconds <= 0 ? 120 : settings.DefaultTimeoutSeconds;
        var maximum = Math.Max(1, settings.BuildTimeoutSeconds);
        var timeout = Math.Clamp(
            requestedTimeoutSeconds <= 0 ? fallback : requestedTimeoutSeconds,
            1,
            maximum);

        return new SkillScriptPolicyResult(
            true,
            string.Empty,
            values,
            WorkspacePath.Relative(workspaceRoot, fullDirectory),
            timeout);
    }

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
            || argument.StartsWith(@"\", StringComparison.Ordinal)
            || argument.StartsWith("~", StringComparison.Ordinal))
        {
            return true;
        }

        return argument.Length >= 2
               && char.IsLetter(argument[0])
               && argument[1] == ':';
    }
}
