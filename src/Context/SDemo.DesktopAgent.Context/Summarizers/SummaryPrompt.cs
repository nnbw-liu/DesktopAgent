using System.Text;
using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 摘要提示词（固定模板，禁止自由格式）。系统提示词只描述任务与格式，
/// 用户消息里放「已有摘要 + 按来源序号标注的新增历史」。
/// </summary>
public static class SummaryPrompt
{
    public const string System = """
        你在压缩一段开发会话的历史。只陈述事实，不写建议、不写推测。
        输出必须且只能包含以下小节，保持顺序：

        ## 目标与约束
        ## 已确认决策
        ## 关键事实
        ## 已改动
        ## 未完成
        ## 用户偏好

        要求：
        - 每条事实末尾用 [m12] 标注来源消息序号；
        - 路径、命令、错误码保留原文，不要改写；
        - 总长度不超过 600 字；
        - 没有内容的小节写「无」；
        - 不得编造未出现过的文件、命令或结论。
        """;

    /// <summary>单条消息进入摘要提示词时的正文上限（避免一条巨型消息吃掉整个输入预算）。</summary>
    public const int MaxCharsPerMessage = 1200;

    /// <summary>拼装用户消息：上一版摘要 + 新增历史（带绝对消息序号）。</summary>
    public static string BuildUserPrompt(
        string? previousSummary,
        IReadOnlyList<ChatMessageModel> chunk,
        int chunkStartOrder,
        int inputMaxTokens)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            builder.AppendLine("【已有摘要（覆盖更早历史）】");
            builder.AppendLine(previousSummary.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("【新增历史（每条前面的 [mN] 是来源序号，引用时必须原样使用）】");

        for (var index = 0; index < chunk.Count; index++)
        {
            var message = chunk[index];
            var order = chunkStartOrder + index;
            var role = message.Role == ChatMessageRole.User ? "用户" : "助手";
            builder.Append('[').Append('m').Append(order).Append("] ").Append(role).Append("：");

            var content = Collapse(message.Content ?? string.Empty);
            builder.AppendLine(content.Length <= MaxCharsPerMessage
                ? content
                : string.Concat(content.AsSpan(0, MaxCharsPerMessage), "…（后文略）"));

            foreach (var trace in message.ToolTrace)
            {
                builder.Append('[').Append('m').Append(order).Append("] 工具：")
                    .AppendLine(ToolTraceFormatter.RenderLine(trace));
            }
        }

        var text = builder.ToString();

        // 调用方已经按 token 分块；这里再加一道字符级保险，避免极端中文文本把 6K 预算顶穿。
        var maxChars = Math.Max(2000, inputMaxTokens * 2);
        return text.Length <= maxChars
            ? text
            : string.Concat(text.AsSpan(0, maxChars), "\n…（更早内容已截断）");
    }

    /// <summary>格式校验失败后的追加提示：只修格式，不要求模型重新理解历史。</summary>
    public static string BuildRepairHint(string? reason) =>
        $"""

        【格式纠正】上一次输出未通过校验：{reason ?? "格式不符"}。
        请重新输出完整摘要，必须严格只包含下面 6 个二级标题（顺序一致，不得增删）：
        {string.Join(" / ", SummaryFormat.Sections)}
        每个非「无」的事实条目必须以 [m序号] 结尾；总长度不超过 600 字。
        """;

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var character in text)
        {
            if (character is '\r' or '\n' or '\t')
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
