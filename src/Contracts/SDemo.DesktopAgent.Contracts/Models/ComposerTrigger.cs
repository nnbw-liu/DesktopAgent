namespace SDemo.DesktopAgent.Models;

/// <summary>输入框里的触发符：<c>@</c> 引用文件，<c>/</c> 命令或技能。</summary>
public enum ComposerTriggerKind
{
    /// <summary>`@path`：把工作区路径插进提示词，只给路径不给内容。</summary>
    Mention,

    /// <summary>`/name`：命令（不进模型）或技能（注入正文）。</summary>
    Command
}

/// <summary>
/// 输入框里正在输入的触发 token。<see cref="Start"/> 指向触发符本身，
/// <see cref="Caret"/> 是光标位置，两者之间的内容是 <see cref="Query"/>。
/// </summary>
public sealed record ComposerTrigger(
    ComposerTriggerKind Kind,
    string Query,
    int Start,
    int Caret,
    bool Quoted)
{
    /// <summary>触发符 + 查询的长度（替换这段即可插入候选）。</summary>
    public int TokenLength => Caret - Start;
}

/// <summary>
/// 触发符的语法：行首或空白之后的 <c>@</c> / <c>/</c> 才算触发；
/// <c>@"带 空格"</c> 允许空格，直到闭合引号为止。
/// <para>
/// 纯规则、不碰 UI：这样 <c>@</c> 不误伤邮箱、<c>/</c> 不误伤「和/或」。
/// </para>
/// </summary>
public static class ComposerTriggers
{
    public const char Mention = '@';

    public const char Command = '/';

    /// <summary>识别光标处的触发 token；没有则返回 null。</summary>
    public static ComposerTrigger? Detect(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        caret = Math.Clamp(caret, 0, text.Length);

        // 引号 mention：`@"...` 还没闭合时，里面允许空格。
        var quoted = DetectQuoted(text, caret);

        if (quoted is not null)
        {
            return quoted;
        }

        // 普通 token：从光标往前找到这一段的开头。
        var start = caret;

        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        if (start >= caret)
        {
            return null;
        }

        var head = text[start];

        if (head is not (Mention or Command))
        {
            return null;
        }

        if (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            return null;
        }

        // `/` 只在行首才算命令，避免「和/或」这种词里冒出菜单。
        if (head == Command && !IsLineStart(text, start))
        {
            return null;
        }

        var token = text[(start + 1)..caret];

        return token.Contains('"')
            ? null
            : new ComposerTrigger(
                head == Mention ? ComposerTriggerKind.Mention : ComposerTriggerKind.Command,
                token,
                start,
                caret,
                Quoted: false);
    }

    /// <summary>把路径格式化成 mention：有空格就加引号，目录保留尾斜杠。</summary>
    public static string FormatMention(string path)
    {
        var normalized = path.Replace('\\', '/');
        var needsQuote = normalized.Any(char.IsWhiteSpace) || normalized.Contains('"');

        return needsQuote ? $"{Mention}\"{normalized}\"" : $"{Mention}{normalized}";
    }

    /// <summary>目录下钻时保留未闭合的引号，菜单可以继续往下一层走。</summary>
    public static string FormatDirectoryMention(string path)
    {
        var normalized = path.Replace('\\', '/');

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return normalized.Any(char.IsWhiteSpace)
            ? $"{Mention}\"{normalized}"
            : $"{Mention}{normalized}";
    }

    /// <summary>光标是否在某一行的第一个非空白位置之前（命令只在这个位置生效）。</summary>
    private static bool IsLineStart(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (text[i] == '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ComposerTrigger? DetectQuoted(string text, int caret)
    {
        for (var i = caret - 1; i >= 1; i--)
        {
            if (text[i] == '\n')
            {
                return null;
            }

            if (text[i] != '"')
            {
                continue;
            }

            if (text[i - 1] != Mention)
            {
                return null;
            }

            // `@"` 之前必须是行首或空白（`a@"b` 不算）。
            if (i - 1 > 0 && !char.IsWhiteSpace(text[i - 2]))
            {
                return null;
            }

            var body = text[(i + 1)..caret];

            return body.Contains('"')
                ? null
                : new ComposerTrigger(ComposerTriggerKind.Mention, body, i - 1, caret, Quoted: true);
        }

        return null;
    }
}
