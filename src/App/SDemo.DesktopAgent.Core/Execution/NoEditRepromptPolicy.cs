using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Execution;

/// <summary>一次“没有改文件”回灌的判定结果。</summary>
public sealed record NoEditRepromptDecision(bool Continue, string? Prompt, int Attempt, int MaxAttempts);

/// <summary>
/// P7 产品化：模型读了一堆文件、却没有任何成功编辑就宣布“完成”时，宿主最多追问 3 次。
/// 只在 Full 模式、任务型输入、确实调用过工具、且没有成功编辑时触发；
/// 纯问答 / 反问 / Compact 模式不打扰。
/// </summary>
public static class NoEditRepromptPolicy
{
    public const int MaxAttempts = 3;

    private static readonly HashSet<string> EditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "apply_patch",
        "replace_text",
        "write_file"
    };

    public static NoEditRepromptDecision Decide(
        AgentRunMetrics metrics,
        string userInput,
        string assistantText,
        int alreadyAttempted,
        bool compact)
    {
        var max = MaxAttempts;

        if (compact
            || alreadyAttempted >= max
            || string.IsNullOrWhiteSpace(assistantText)
            || TurnClassifier.Classify(userInput) == TurnIntent.Chat
            || !LooksLikeEditTask(userInput)
            || metrics.ToolTrace.Count == 0
            || HasSuccessfulEdit(metrics.ToolTrace)
            || LooksLikeQuestion(assistantText))
        {
            return new NoEditRepromptDecision(false, null, alreadyAttempted, max);
        }

        var attempt = alreadyAttempted + 1;
        var prompt = attempt == 1
            ? "[宿主提示] 本轮没有检测到任何文件修改，任务尚未完成。"
              + "请先用 search_files / glob_files 定位文件，read_file 后用 apply_patch 或 replace_text 写入最小改动；"
              + "不要只输出计划或声称已经完成。"
            : $"[宿主提示] 仍然没有检测到文件修改（第 {attempt}/{max} 次提醒）。"
              + "不要再重复读取同一文件；如果补丁失败，请按错误里的「最近相似片段 / 候选补丁」改一处再提交；"
              + "如果确实无法完成，请明确说明卡在哪一步。";

        return new NoEditRepromptDecision(true, prompt, attempt, max);
    }

    /// <summary>
    /// 是否像“要动文件”的任务。先看明确的动手指令；只在提问 / 解释 / 检查某一处「怎么改」时
    /// 不算任务，避免把只读提问拽进编辑循环。泛泛提到「改」但没有指令语气也不追问。
    /// </summary>
    private static bool LooksLikeEditTask(string input)
    {
        var text = input.ToLowerInvariant();

        // 动手指令优先；只有「怎么改」「改哪里」这类疑问才不算任务。
        if (TurnClassifier.HasImperativeAction(text))
        {
            return true;
        }

        if (IsReadOnlyQuestion(text))
        {
            return false;
        }

        return CapabilityRouter.LooksLikeTask(input);
    }

    private static readonly string[] ReadOnlyWords =
    [
        "解释", "说明", "总结", "阅读", "看一下", "看看", "查看", "检查", "分析", "为什么", "是什么",
        "怎么理解", "干什么", "做什么", "作用", "用途", "介绍", "讲讲", "怎么用", "对比", "哪里",
        "是否", "能不能", "对不对", "行不行"
    ];

    private static bool IsReadOnlyQuestion(string text) =>
        text.Contains('?', StringComparison.Ordinal)
        || text.Contains('？', StringComparison.Ordinal)
        || ReadOnlyWords.Any(word => text.Contains(word, StringComparison.Ordinal));

    /// <summary>成功编辑 = 编辑工具且轨迹状态为 ok，并且解析出了 [EDIT] 行。</summary>
    public static bool HasSuccessfulEdit(IReadOnlyList<ToolTraceEntry> trace) =>
        trace.Any(entry =>
            EditTools.Contains(entry.ToolId)
            && string.Equals(entry.Status, ToolTraceEntry.StatusOk, StringComparison.OrdinalIgnoreCase)
            && entry.Edits is { Count: > 0 });

    private static bool LooksLikeQuestion(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith('?')
            || trimmed.EndsWith('？')
            || trimmed.Contains("请提供", StringComparison.Ordinal)
            || trimmed.Contains("需要你确认", StringComparison.Ordinal);
    }
}
