using System.Text;
using System.Text.RegularExpressions;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>一次解析的结果：成对段落 + 纯译文 + 是否还有未闭合的尾部标签。</summary>
public sealed record SegmentedParseResult(
    IReadOnlyList<OutputSegment> Segments,
    string Text,
    bool HasPairs,
    bool Pending)
{
    public bool HasSegments => Segments.Count > 0;
}

/// <summary>
/// 解析双语对照的 <c>&lt;s&gt;原文&lt;/s&gt;&lt;t&gt;译文&lt;/t&gt;</c> 输出（见设计文档 §10.2）。
/// 规则：
/// <list type="bullet">
/// <item>按出现顺序把 <c>s</c> / <c>t</c> 配成对；</item>
/// <item>数量不等时以较少的为准，多出来的 <c>&lt;t&gt;</c> 并入最后一段译文；</item>
/// <item>流式中未闭合的尾部标签先丢弃，<see cref="SegmentedParseResult.Pending"/> 为 true；</item>
/// <item>一条完整配对都没有 → 降级：整段输出当译文（<see cref="SegmentedParseResult.HasPairs"/> 为 false）。</item>
/// </list>
/// </summary>
public static class SegmentedOutputParser
{
    private static readonly Regex PairPattern = new(
        @"<(s|t)>\s*([\s\S]*?)\s*</\1>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SegmentedParseResult Parse(string? raw, bool final = false)
    {
        var text = raw ?? string.Empty;
        if (text.Length == 0)
        {
            return new SegmentedParseResult([], string.Empty, false, false);
        }

        var segments = new List<OutputSegment>();
        string? pendingSource = null;
        var lastCompleteEnd = 0;

        foreach (Match match in PairPattern.Matches(text))
        {
            lastCompleteEnd = match.Index + match.Length;
            var kind = match.Groups[1].Value;
            var content = Normalize(match.Groups[2].Value);

            if (string.Equals(kind, "s", StringComparison.Ordinal))
            {
                // 上一个 <s> 没有等到 </t>：以较少的为准，丢掉。
                pendingSource = content;
                continue;
            }

            if (pendingSource is not null)
            {
                segments.Add(new OutputSegment(pendingSource, content));
                pendingSource = null;
                continue;
            }

            // 多出来的 <t>：并入最后一段译文；没有上一段就当成只有译文的段落。
            if (segments.Count > 0)
            {
                var last = segments[^1];
                segments[^1] = last with
                {
                    Target = last.Target.Length == 0 ? content : last.Target + "\n\n" + content
                };
            }
            else
            {
                segments.Add(new OutputSegment(string.Empty, content));
            }
        }

        // 尾部还有没闭合的 <s> / <t>：流式中先不展示，标记为 pending。
        var tail = text[lastCompleteEnd..];
        var pending = !final
                      && (tail.Contains("<s>", StringComparison.Ordinal)
                          || tail.Contains("<t>", StringComparison.Ordinal));

        if (segments.Count == 0)
        {
            // 降级：模型没按格式输出，整段当译文；流式中还没解析出任何完整段落时先留空，
            // 避免把 "<s>原文" 这种半截标签显示出来。收尾时把残留标签去掉再展示。
            var degraded = pending ? string.Empty : StripTags(text).Trim();
            return new SegmentedParseResult([], degraded, false, pending);
        }

        var targetText = string.Join(
            "\n\n",
            segments.Select(segment => segment.Target).Where(target => target.Length > 0));

        return new SegmentedParseResult(segments, targetText, true, pending);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>去掉残留的 <c>&lt;s&gt;</c> / <c>&lt;t&gt;</c> 标记，用于降级展示。</summary>
    private static string StripTags(string value) =>
        value.Replace("<s>", string.Empty, StringComparison.Ordinal)
             .Replace("</s>", string.Empty, StringComparison.Ordinal)
             .Replace("<t>", string.Empty, StringComparison.Ordinal)
             .Replace("</t>", string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// 流式累积器：把增量文本攒起来，随时解析出当前可展示的段落。
/// 字数上限到了之后不再追加（<see cref="Truncated"/> 为 true），避免异常输出撑爆内存。
/// </summary>
public sealed class SegmentedOutputAccumulator
{
    private readonly StringBuilder _raw = new();
    private readonly int _maxChars;

    public SegmentedOutputAccumulator(int maxChars = 32_000) => _maxChars = Math.Max(1, maxChars);

    public bool Truncated { get; private set; }

    public string RawText => _raw.ToString();

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_raw.Length >= _maxChars)
        {
            Truncated = true;
            return;
        }

        var allowed = Math.Min(text.Length, _maxChars - _raw.Length);
        _raw.Append(text, 0, allowed);

        if (allowed < text.Length)
        {
            Truncated = true;
        }
    }

    public SegmentedParseResult Parse(bool final = false) =>
        SegmentedOutputParser.Parse(_raw.ToString(), final);
}
