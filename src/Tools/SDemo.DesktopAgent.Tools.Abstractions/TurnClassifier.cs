using System.Text.RegularExpressions;
using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 成本档 0 的「先决定做什么」：把一轮输入分成对话 / 问答 / 任务。
/// 只回答一个问题——本轮到底需不需要工具；具体要哪些工具仍交给 <see cref="CapabilityRouter"/>。
/// 判定必须保守：宁可多给工具，也不要把「帮我看下这个文件」误判成聊天。
/// </summary>
public static class TurnClassifier
{
    /// <summary>寒暄类短语只在整句很短时才算对话（避免「你好，帮我改个文件」被判成聊天）。</summary>
    public const int MaxChatLength = 16;

    /// <summary>用户明确说不用工具：即使句子里有「文件」「代码」也直接聊天。</summary>
    private static readonly string[] NoToolPhrases =
    [
        "不要用工具", "别用工具", "不要调用工具", "别调用工具", "不用工具",
        "不要查文件", "不用查文件", "不要看文件", "不用看文件",
        "不要搜索", "不用搜索", "只回答", "直接回答"
    ];

    /// <summary>寒暄 / 致谢 / 身份询问 / 闲聊：这些轮次不需要看工作区。</summary>
    private static readonly string[] ChatPhrases =
    [
        "你好", "您好", "hi", "hello", "hey", "嗨", "哈喽", "在吗", "在么",
        "早上好", "下午好", "晚上好", "早安", "晚安",
        "谢谢", "谢了", "感谢", "多谢", "thanks", "thank you", "thx",
        "好的", "好滴", "行", "嗯", "哦", "噢", "收到", "明白了", "知道了", "没问题",
        "再见", "拜拜", "bye",
        "你是谁", "你叫什么", "你能做什么", "你会什么", "你能干什么", "你是干什么的",
        "介绍下你自己", "介绍一下你自己", "自我介绍",
        "辛苦了", "厉害了", "不错", "挺好",
        "聊两句", "聊聊天", "随便聊", "讲个笑话", "说个笑话"
    ];

    /// <summary>
    /// 明确的动手指令：「帮我改…」「把 X 改成 Y」「修一下」「实现登录」「跑一下构建」。
    /// 只命中「怎么改」「改哪里」这类疑问句时不算任务，避免把只读提问拽进编辑循环。
    /// </summary>
    private static readonly Regex ImperativeAction = new(
        @"(?:帮我|给我|请你|麻烦你?|把|替我把?)[^。！？!?]{0,10}"
        + @"(?:改|修|加|删|替换|实现|新增|添加|创建|新建|生成|写|补|重构|优化|清理|安装|配置|部署|运行|执行|跑|导出|翻译|整理|转换)|"
        + @"(?:改|修|加|删|替换|实现|新增|添加|创建|新建|生成|补|重构|优化|清理|安装|配置|部署|运行|执行|跑|导出|翻译|整理|转换)"
        + @"(?:一下|一遍|一个|个|成|掉|到)|"
        + @"^(?:改|修|加|删|替换|实现|新增|添加|创建|新建|生成|写|补|重构|优化|清理|安装|配置|部署|运行|执行|跑|导出|翻译|整理)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 判定本轮类型。<paramref name="hasSelectedSkill"/> 为 true 表示用户手选了技能，
    /// 属于「明确要用能力」，不按聊天处理。
    /// </summary>
    public static TurnIntent Classify(string? input, bool hasSelectedSkill = false)
    {
        var text = (input ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return TurnIntent.Chat;
        }

        if (hasSelectedSkill)
        {
            return TurnIntent.Task;
        }

        if (ContainsAny(text, NoToolPhrases))
        {
            return TurnIntent.Chat;
        }

        var lower = text.ToLowerInvariant();

        if (lower.Length <= MaxChatLength && ChatPhrases.Any(phrase => IsChatPhrase(lower, phrase)))
        {
            return TurnIntent.Chat;
        }

        return ImperativeAction.IsMatch(lower) ? TurnIntent.Task : TurnIntent.Answer;
    }

    /// <summary>是否只是闲聊 / 寒暄（供宿主决定要不要追问、要不要注入工具）。</summary>
    public static bool IsChat(string? input) => Classify(input) == TurnIntent.Chat;

    /// <summary>是否含明确的动手指令（「帮我改…」「修一下」「实现…」）。</summary>
    public static bool HasImperativeAction(string? input) =>
        !string.IsNullOrWhiteSpace(input) && ImperativeAction.IsMatch(input.ToLowerInvariant());

    private static bool IsChatPhrase(string lower, string phrase) =>
        lower == phrase
        || (lower.StartsWith(phrase, StringComparison.Ordinal)
            && lower.Length <= phrase.Length + 4);

    private static bool ContainsAny(string text, IReadOnlyList<string> words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
