using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Execution;

/// <summary>是否追加一次宿主验证提示（user 角色，不伪造 tool 消息）。</summary>
public sealed record AutoContinueDecision(bool Continue, string? Prompt, int MaxAttempts);

public static class AutoContinuePolicy
{
    public static AutoContinueDecision Decide(
        ChatSession session,
        string? buildCommand,
        string? testCommand,
        VerificationSettings settings,
        int alreadyContinued,
        bool hasNewEdits = true)
    {
        var gate = VerificationGate.Evaluate(session);
        var max = Math.Clamp(settings.MaxAutoContinue, 0, 1);

        if (!gate.Dirty
            || !hasNewEdits
            || !settings.AutoVerifyAfterEdit
            || alreadyContinued >= max
            || (string.IsNullOrWhiteSpace(buildCommand) && string.IsNullOrWhiteSpace(testCommand)))
        {
            return new AutoContinueDecision(false, null, max);
        }

        var command = !string.IsNullOrWhiteSpace(buildCommand)
            ? buildCommand
            : testCommand!;

        return new AutoContinueDecision(
            true,
            $"[宿主提示] 工作区有未验证改动，请先运行 `{command}` 并修复错误后再结束；"
            + "如果无法运行，请明确说明未验证原因。",
            max);
    }
}
