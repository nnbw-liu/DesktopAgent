using System.Text;

namespace SDemo.DesktopAgent.Models;

/// <summary>一段已插入的引用 / 命令 token 的类型。</summary>
public enum ComposerTokenKind
{
    /// <summary>`@path` / `@"带 空格"`。</summary>
    Mention,

    /// <summary>行首的 `/name`。</summary>
    Command
}

/// <summary>文本里的一段 token（只用于高亮与提交时的识别，不改变文本本身）。</summary>
public sealed record ComposerToken(int Start, int Length, ComposerTokenKind Kind, string Text);

/// <summary>
/// 扫描已经写进输入框的 token：`@` 与行首 `/`。
/// <para>
/// 与 <see cref="ComposerTriggers"/>（正在输入什么）互补：这里看的是**已经落下的文本**，
/// 用来画高亮、以及在发送时认出「用户点名了哪个技能」。
/// </para>
/// </summary>
public static class ComposerTokens
{
    /// <summary>按出现顺序扫描；重叠的情况以先出现的 token 为准。</summary>
    public static IReadOnlyList<ComposerToken> Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var tokens = new List<ComposerToken>();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];
            var atBoundary = index == 0 || char.IsWhiteSpace(text[index - 1]);

            if (current == ComposerTriggers.Mention && atBoundary)
            {
                var end = ScanMention(text, index);
                tokens.Add(new ComposerToken(index, end - index, ComposerTokenKind.Mention, text[index..end]));
                index = end;
                continue;
            }

            if (current == ComposerTriggers.Command && IsLineStart(text, index))
            {
                var end = ScanToken(text, index);

                if (end > index + 1)
                {
                    tokens.Add(new ComposerToken(index, end - index, ComposerTokenKind.Command, text[index..end]));
                }

                index = end;
                continue;
            }

            index++;
        }

        return tokens;
    }

    /// <summary>
    /// 提交时认出被点名的技能：任意空白分隔的 `/name` 都算（命令才要求行首）。
    /// 与 DSH 的 `/name` 手势一致——技能可以在句中指名。
    /// </summary>
    public static string? FindInvokedSkill(string? text, IEnumerable<string> knownSkills)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var names = new HashSet<string>(knownSkills, StringComparer.OrdinalIgnoreCase);

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2 || token[0] != ComposerTriggers.Command)
            {
                continue;
            }

            var name = token[1..].Trim();

            if (names.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>把文本渲染成高亮用的 HTML：token 包一层 span，其余原样转义。</summary>
    public static string ToHighlightHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 64);
        var position = 0;

        foreach (var token in Scan(text))
        {
            if (token.Start > position)
            {
                builder.Append(Escape(text[position..token.Start]));
            }

            var css = token.Kind == ComposerTokenKind.Mention ? "mention" : "command";
            builder.Append("<span class=\"composer-token composer-token--")
                .Append(css)
                .Append("\">")
                .Append(Escape(token.Text))
                .Append("</span>");

            position = token.Start + token.Length;
        }

        if (position < text.Length)
        {
            builder.Append(Escape(text[position..]));
        }

        // 末尾换行在 block 里会被折叠，补一个零宽字符保住最后一行的位置。
        builder.Append('​');

        return builder.ToString();
    }

    private static int ScanMention(string text, int start)
    {
        var after = start + 1;

        if (after < text.Length && text[after] == '"')
        {
            var close = text.IndexOf('"', after + 1);
            var lineEnd = text.IndexOf('\n', after + 1);

            if (close > 0 && (lineEnd < 0 || close < lineEnd))
            {
                return close + 1;
            }

            return lineEnd < 0 ? text.Length : lineEnd;
        }

        return ScanToken(text, start);
    }

    private static int ScanToken(string text, int start)
    {
        var end = start + 1;

        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return end;
    }

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

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
