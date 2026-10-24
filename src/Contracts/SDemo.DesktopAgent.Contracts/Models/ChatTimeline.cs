using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 把一次生成的「思考 / 工具 / 正文」还原成时间线。
/// <para>
/// 实时生成时由界面按到达顺序直接记步骤；重开会话时没有实时步骤，
/// 就按工具轨迹里记下的字符边界（<see cref="ToolTraceEntry.ReasoningChars"/> /
/// <see cref="ToolTraceEntry.TextChars"/>）把两段长文本切开重新排。
/// </para>
/// </summary>
public static class ChatTimelineBuilder
{
    /// <summary>旧消息没有边界信息时返回空集合，界面回退到分组显示。</summary>
    public static IReadOnlyList<ChatStep> FromTrace(
        string reasoning,
        string content,
        IReadOnlyList<ToolTraceEntry> trace)
    {
        if (trace.Count == 0 || trace.All(entry => entry.ReasoningChars < 0))
        {
            return [];
        }

        reasoning ??= string.Empty;
        content ??= string.Empty;

        var steps = new List<ChatStep>(trace.Count * 2 + 2);
        var reasoningRead = 0;
        var textRead = 0;

        for (var index = 0; index < trace.Count; index++)
        {
            var entry = trace[index];
            var reasoningTo = entry.ReasoningChars >= 0 ? entry.ReasoningChars : reasoningRead;
            var textTo = entry.TextChars >= 0 ? entry.TextChars : textRead;

            AppendSlice(steps, reasoning, ref reasoningRead, reasoningTo, ChatStepKind.Reasoning);
            AppendSlice(steps, content, ref textRead, textTo, ChatStepKind.Text);
            steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, index));
        }

        AppendSlice(steps, reasoning, ref reasoningRead, reasoning.Length, ChatStepKind.Reasoning);
        AppendSlice(steps, content, ref textRead, content.Length, ChatStepKind.Text);

        return steps;
    }

    private static void AppendSlice(
        List<ChatStep> steps,
        string source,
        ref int read,
        int to,
        ChatStepKind kind)
    {
        var end = Math.Clamp(to, read, source.Length);

        if (end > read)
        {
            steps.Add(new ChatStep(kind, source[read..end]));
            read = end;
        }
    }
}
