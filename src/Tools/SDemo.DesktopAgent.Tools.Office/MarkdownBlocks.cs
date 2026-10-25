using System.Text;
using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// Office 写入用的极简 Markdown 解析：只认标题、段落、列表、表格、代码块、引用与分隔线。
/// <para>
/// 不用完整的 Markdig AST，是因为写 Office 只需要这几种块；自己解析可以确保
/// 模型常见的半结构化输出（中文标点、缺空行、表格前后没有空行）也能稳定落盘。
/// </para>
/// </summary>
internal static partial class MarkdownBlocks
{
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            // 代码块
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new StringBuilder();
                while (index < lines.Length
                       && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }

                index++; // 跳过结束围栏
                blocks.Add(new MarkdownCode(code.ToString().TrimEnd('\n')));
                continue;
            }

            // 表格：连续的 | ... | 行；第二行是分隔行
            if (IsTableRow(line) && index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                var rows = new List<IReadOnlyList<string>> { SplitTableRow(line) };
                index += 2;

                while (index < lines.Length && IsTableRow(lines[index]))
                {
                    rows.Add(SplitTableRow(lines[index]));
                    index++;
                }

                blocks.Add(new MarkdownTable(rows));
                continue;
            }

            // 标题
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                blocks.Add(new MarkdownHeading(heading.Groups[1].Length, CleanInline(heading.Groups[2].Value)));
                index++;
                continue;
            }

            // 分隔线（Word 里当分页，PPT 里当分页，Excel 忽略）
            if (RulePattern().IsMatch(line))
            {
                blocks.Add(new MarkdownRule());
                index++;
                continue;
            }

            // 引用
            if (QuotePattern().Match(line) is { Success: true } quote)
            {
                var text = new StringBuilder(quote.Groups[1].Value);
                index++;

                while (index < lines.Length && QuotePattern().Match(lines[index]) is { Success: true } next)
                {
                    text.Append(' ').Append(next.Groups[1].Value);
                    index++;
                }

                blocks.Add(new MarkdownQuote(CleanInline(text.ToString())));
                continue;
            }

            // 列表（连续同类或不同类都收进同一个 ListBlock；层级由缩进决定）
            if (ListPattern().Match(line) is { Success: true } item)
            {
                var items = new List<MarkdownListItem>();

                while (index < lines.Length && ListPattern().Match(lines[index]) is { Success: true } current)
                {
                    var indent = current.Groups[1].Value.Replace("\t", "    ").Length;
                    var ordered = current.Groups[2].Value.Length > 1
                        || char.IsDigit(current.Groups[2].Value[0]);
                    items.Add(new MarkdownListItem(
                        Math.Clamp(indent / 2, 0, 4),
                        ordered,
                        CleanInline(current.Groups[3].Value)));
                    index++;
                }

                blocks.Add(new MarkdownList(items));
                continue;
            }

            // 普通段落：吃到空行或下一个块
            var paragraph = new StringBuilder(line);
            index++;

            while (index < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[index])
                   && !StartsBlock(lines[index])
                   && !IsTableRow(lines[index]))
            {
                paragraph.Append(' ').Append(lines[index].Trim());
                index++;
            }

            blocks.Add(new MarkdownParagraph(CleanInline(paragraph.ToString())));
        }

        return blocks;
    }

    /// <summary>把 <c>**粗体**</c>、<c>*斜体*</c>、<c>`代码`</c>、<c>[文字](链接)</c> 拆成带样式的片段。</summary>
    public static IReadOnlyList<MarkdownSpan> ParseInline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var value = text ?? string.Empty;
        var position = 0;

        foreach (Match match in InlinePattern().Matches(value))
        {
            if (match.Index > position)
            {
                spans.Add(new MarkdownSpan(value[position..match.Index], false, false, false));
            }

            var bold = match.Groups["bold"].Success;
            var italic = match.Groups["italic"].Success;
            var code = match.Groups["code"].Success;
            var link = match.Groups["link"].Success;

            var content = bold
                ? match.Groups["bold"].Value
                : italic
                    ? match.Groups["italic"].Value
                    : code
                        ? match.Groups["code"].Value
                        : match.Groups["linkText"].Value;

            spans.Add(new MarkdownSpan(content, bold, italic, code));
            position = match.Index + match.Length;

            if (link)
            {
                // 链接地址不进正文；保留可读文字即可。
            }
        }

        if (position < value.Length)
        {
            spans.Add(new MarkdownSpan(value[position..], false, false, false));
        }

        return spans.Count == 0 ? [new MarkdownSpan(value, false, false, false)] : spans;
    }

    public static string CleanInline(string text)
    {
        var spans = ParseInline(text);
        var builder = new StringBuilder();

        foreach (var span in spans)
        {
            builder.Append(span.Text);
        }

        return builder.ToString().Trim();
    }

    private static bool StartsBlock(string line) =>
        HeadingPattern().IsMatch(line)
        || RulePattern().IsMatch(line)
        || QuotePattern().IsMatch(line)
        || ListPattern().IsMatch(line)
        || line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '|' && trimmed[^1] == '|';
    }

    private static bool IsTableSeparator(string line)
    {
        if (!IsTableRow(line))
        {
            return false;
        }

        var cells = SplitTableRow(line);
        return cells.Count > 0 && cells.All(cell =>
            cell.Length > 0 && cell.All(character => character is '-' or ':' or ' '));
    }

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed
            .Split('|')
            .Select(cell => CleanInline(cell.Replace("\\|", "|")))
            .ToArray();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^\s*>\s?(.*)$")]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"^([ \t]*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(
        @"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`|\*(?<italic>[^*]+)\*|\[(?<linkText>[^\]]+)\]\((?<link>[^)]+)\)")]
    private static partial Regex InlinePattern();
}

internal abstract record MarkdownBlock;

internal sealed record MarkdownHeading(int Level, string Text) : MarkdownBlock;

internal sealed record MarkdownParagraph(string Text) : MarkdownBlock;

internal sealed record MarkdownList(IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

internal sealed record MarkdownListItem(int Level, bool Ordered, string Text);

internal sealed record MarkdownTable(IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock;

internal sealed record MarkdownCode(string Text) : MarkdownBlock;

internal sealed record MarkdownQuote(string Text) : MarkdownBlock;

internal sealed record MarkdownRule : MarkdownBlock;

internal sealed record MarkdownSpan(string Text, bool Bold, bool Italic, bool Code);
