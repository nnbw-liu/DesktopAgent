using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 摘要提示词（固定模板，禁止自由格式）。
/// <para>
/// P2b §3.3.2 第二条：**摘要请求复用热前缀**。DSH 的做法是「逐字回放 system + tools + 被遮蔽区域，
/// 最后追加一段压缩指令」（<c>dsh-compaction-basic</c> 的 <c>COMPACTION_INSTRUCTION</c>），
/// 这样摘要调用是上一次请求的真前缀，端点的 KV 缓存能直接用上。我们照这个形状做：
/// </para>
/// <code>
/// [system] 会话原有的提示词（逐字，不换成摘要器自己的）
/// [user] / [assistant] … 被遮蔽区域逐条回放，role 与内容都保持原样
/// [user] 压缩指令（格式模板 + 来源标注规则）
/// </code>
/// <para>
/// 旧做法（把历史压成一段带 [mN] 标签的文本、系统提示词换成摘要专用）没有前缀可复用，
/// 只剩「一次额外的全量输入」。只有当调用方拿不到会话提示词时，才退回旧做法（<see cref="System"/>）。
/// </para>
/// </summary>
public static class SummaryPrompt
{
    /// <summary>拿不到会话提示词时的兜底系统提示词（这条路径没有热前缀可复用）。</summary>
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

    /// <summary>
    /// 压缩指令：作为回放区域之后的**最后一条 user 消息**（照 DSH 的位置）。
    /// 会话正文在前、任务说明在后——模型对最后一条指令的响应最可靠，前缀也能被缓存复用。
    /// </summary>
    public const string Instruction = """
        现在请把上面的对话压缩成一份结构化检查点，让另一个模型在不看原文的情况下继续这项工作。
        只陈述事实，不写建议、不写推测。输出必须且只能包含以下小节，保持顺序：

        ## 目标与约束
        ## 已确认决策
        ## 关键事实
        ## 已改动
        ## 未完成
        ## 用户偏好

        要求：
        - 来源标注写成 [m1]、[m2]……：数字是**上面这段历史里的第几条消息**（第一条是 m1，从 1 开始数）；
        - 路径、命令、错误码保留原文，不要改写；
        - 总长度不超过 600 字；
        - 没有内容的小节写「无」；
        - 不得编造未出现过的文件、命令或结论。
        """;

    /// <summary>单条消息进入摘要提示词时的正文上限（避免一条巨型消息吃掉整个输入预算）。</summary>
    public const int MaxCharsPerMessage = 1200;

    /// <summary>把一次摘要请求拼成消息列表：system → 回放区域 → 压缩指令（+ 可选的格式纠正）。</summary>
    public static IReadOnlyList<AIChatMessage> BuildMessages(SummaryRequest request, string? repairHint = null)
    {
        var messages = new List<AIChatMessage>(request.Chunk.Count + 2);
        var instructions = string.IsNullOrWhiteSpace(request.Instructions)
            ? System
            : request.Instructions!;

        messages.Add(new AIChatMessage(ChatRole.System, instructions));

        // 已有摘要作为一条 assistant 消息回放（它是上一轮压缩的产物，原文已经不在请求里了）。
        if (!string.IsNullOrWhiteSpace(request.PreviousSummary))
        {
            messages.Add(new AIChatMessage(
                ChatRole.Assistant,
                "【已有摘要（覆盖更早历史）】\n" + request.PreviousSummary.Trim()));
        }

        for (var index = 0; index < request.Chunk.Count; index++)
        {
            messages.Add(Render(request.Chunk[index], request.AttachmentCharLimit));
        }

        var instruction = string.IsNullOrWhiteSpace(repairHint)
            ? Instruction
            : Instruction + repairHint;

        messages.Add(new AIChatMessage(ChatRole.User, instruction));
        return messages;
    }

    /// <summary>
    /// 逐条回放：正文按对话侧同一个渲染器产出（附件改桩、轨迹不回放），role 保持原样。
    /// <para>
    /// 被遮蔽区域永远是**最旧**的一段，真实请求里它本来就拿不到轨迹预算、附件也早就是桩，
    /// 所以这里按同样口径渲染，通常与上次请求逐字节相同。
    /// </para>
    /// </summary>
    private static AIChatMessage Render(ChatMessageModel message, int attachmentCharLimit)
    {
        var role = message.Role == ChatMessageRole.User ? ChatRole.User : ChatRole.Assistant;
        var text = Services.AgentMessageMapper.Render(message, Math.Max(0, attachmentCharLimit), null).Text;

        // 单条超长消息只保留头部：它不是本轮新增内容，摘要不需要全文。
        if (text.Length > MaxCharsPerMessage)
        {
            text = string.Concat(text.AsSpan(0, MaxCharsPerMessage), "…（后文略）");
        }

        return new AIChatMessage(role, text);
    }

    /// <summary>旧路径（无热前缀）用的拼接方式：上一版摘要 + 新增历史（带绝对消息序号）。</summary>
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
