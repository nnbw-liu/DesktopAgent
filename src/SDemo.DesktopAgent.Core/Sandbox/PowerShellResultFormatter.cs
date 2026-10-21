using System.Text;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把沙箱作业结果格式化成工具返回文本。模型据此判断发生了什么；
/// 变更集只是“沙箱内的改动”，实际写回必须由用户在界面确认。
/// </summary>
public static class PowerShellResultFormatter
{
    /// <summary>单段输出进入模型上下文的上限，沙箱本身另有 8MB 硬上限。</summary>
    public const int MaxOutputCharsPerStream = 12_000;

    public static string Format(SandboxJobOutcome outcome)
    {
        var result = outcome.Result;

        if (result.Failure is SandboxFailureCode.NotAvailable)
        {
            return $"ERROR: 沙箱不可用，PowerShell 已禁用。{Suffix(result.Message)}";
        }

        if (result.Failure is SandboxFailureCode.WorkspaceTooLarge)
        {
            return $"ERROR: 工作区过大（{result.Message}），未执行。";
        }

        if (result.Failure is SandboxFailureCode.ProtocolError)
        {
            return $"ERROR: 沙箱返回了无效结果。{Suffix(result.Message)}";
        }

        if (result.Failure is SandboxFailureCode.LaunchFailed)
        {
            return $"ERROR: 沙箱启动失败：{result.Message}";
        }

        if (result.Failure is SandboxFailureCode.PathEscape)
        {
            return $"ERROR: 路径超出允许范围。{Suffix(result.Message)}";
        }

        var builder = new StringBuilder();
        builder.Append("沙箱：").AppendLine(result.Kind.ToString());
        builder.Append("退出码：").AppendLine(result.ExitCode.ToString());

        if (result.TimedOut)
        {
            builder.AppendLine("已终止进程树；以下是截止终止前的输出。");
        }

        if (result.Canceled)
        {
            builder.AppendLine("用户取消了这次执行；以下是截止取消前的输出。");
        }

        builder.Append("超时：").AppendLine(result.TimedOut ? "是" : "否");
        builder.Append("耗时：").Append(result.Duration.TotalMilliseconds.ToString("0")).AppendLine(" ms");

        AppendStream(builder, "stdout", result.StdOut);
        AppendStream(builder, "stderr", result.StdErr);

        builder.AppendLine();

        if (outcome.Changes.Count == 0)
        {
            builder.AppendLine("沙箱内没有文件改动。");
        }
        else
        {
            builder.AppendLine("--- 沙箱内改动（尚未写入真实工作区）---");

            foreach (var change in outcome.Changes
                         .OrderBy(change => change.RelativePath, StringComparer.Ordinal))
            {
                builder.Append(ChangeMark(change.Kind))
                    .Append(' ')
                    .AppendLine(change.RelativePath);
            }

            builder.AppendLine();
            builder.AppendLine("请在界面中确认应用这些改动；不要声称已经写入真实工作区。");
        }

        if (result.Failure is not SandboxFailureCode.None && result.Failure != SandboxFailureCode.Timeout)
        {
            builder.AppendLine();
            builder.Append("执行未完成（").Append(result.Failure).Append("）：")
                .AppendLine(result.Message ?? "没有更多信息。");
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            builder.AppendLine();
            builder.AppendLine(result.Message);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendStream(StringBuilder builder, string name, string text)
    {
        builder.AppendLine();
        builder.Append("--- ").Append(name).AppendLine(" ---");

        if (string.IsNullOrEmpty(text))
        {
            builder.AppendLine("（空）");
            return;
        }

        if (text.Length > MaxOutputCharsPerStream)
        {
            builder.AppendLine(text[..MaxOutputCharsPerStream]);
            builder.AppendLine("…（输出已截断）");
            return;
        }

        builder.AppendLine(text.TrimEnd());
    }

    private static string ChangeMark(SandboxChangeKind kind) => kind switch
    {
        SandboxChangeKind.Added => "A",
        SandboxChangeKind.Modified => "M",
        _ => "D"
    };

    private static string Suffix(string? message) =>
        string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
}
