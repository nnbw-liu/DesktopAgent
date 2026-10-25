using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Context;

/// <summary>摘要格式的校验结果；<see cref="Ok"/> 为 false 时调用方必须走确定性兜底。</summary>
public sealed record SummaryValidation(bool Ok, string? Reason)
{
    public static SummaryValidation Pass { get; } = new(true, null);

    public static SummaryValidation Fail(string reason) => new(false, reason);
}

/// <summary>
/// 摘要的固定模板与校验（P2 §3.2）：6 个小节、顺序固定、每条事实带 <c>[m序号]</c> 来源、
/// 总长不超过 <c>SummaryMaxTokens</c>。模板之外的自由格式一律视为失败并走兜底。
/// </summary>
public static class SummaryFormat
{
    /// <summary>兜底摘要的开头标记；UI 据此标注「自动摘要」。</summary>
    public const string FallbackMarker = "（自动摘要，未使用模型）";

    /// <summary>模型摘要必须包含的小节标题，顺序必须一致。</summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        "## 目标与约束",
        "## 已确认决策",
        "## 关键事实",
        "## 已改动",
        "## 未完成",
        "## 用户偏好"
    ];

    private static readonly Regex SourcePattern = new(
        @"\[m(?<order>\d+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>校验模型摘要；<paramref name="requireSources"/> 为 true 时每条事实都必须带来源序号。</summary>
    public static SummaryValidation Validate(
        string? text,
        int maxTokens,
        ITokenCounter counter,
        bool requireSources = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SummaryValidation.Fail("摘要为空");
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var headings = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            // 模板之外的额外二级标题也算格式不符：模型必须且只能输出这 6 节。
            if (!Sections.Contains(line, StringComparer.Ordinal))
            {
                return SummaryValidation.Fail($"出现模板之外的小节：{line}");
            }

            headings.Add(line);
        }

        if (headings.Count != Sections.Count)
        {
            return SummaryValidation.Fail($"小节数量应为 {Sections.Count}，实际 {headings.Count}");
        }

        for (var index = 0; index < Sections.Count; index++)
        {
            if (!string.Equals(headings[index], Sections[index], StringComparison.Ordinal))
            {
                return SummaryValidation.Fail($"小节顺序不符：第 {index + 1} 节应为 {Sections[index]}");
            }
        }

        if (counter.Count(text) > maxTokens)
        {
            return SummaryValidation.Fail($"摘要超过 {maxTokens} token");
        }

        if (requireSources)
        {
            var facts = lines
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Where(line => !string.Equals(line, "- 无", StringComparison.Ordinal))
                .ToList();

            if (facts.Count == 0)
            {
                return SummaryValidation.Fail("没有任何事实条目");
            }

            foreach (var fact in facts)
            {
                var match = SourcePattern.Match(fact);
                if (!match.Success)
                {
                    return SummaryValidation.Fail($"事实缺少来源序号：{Truncate(fact, 40)}");
                }

                if (!int.TryParse(match.Groups["order"].Value, out _))
                {
                    return SummaryValidation.Fail($"来源序号无法解析：{match.Value}");
                }
            }
        }

        return SummaryValidation.Pass;
    }

    /// <summary>
    /// 把摘要截到 token 上限：按行边界截断，并追加一行标记。
    /// 用于模型返回超长、以及读回库里的旧摘要比当前设置更长的情况。
    /// </summary>
    public static string Cap(string? text, int maxTokens, ITokenCounter counter)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Trim();
        var limit = Math.Max(32, maxTokens);

        if (counter.Count(normalized) <= limit)
        {
            return normalized;
        }

        const string suffix = "\n…（摘要已截断）";

        var low = 0;
        var high = normalized.Length;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = normalized[..mid] + suffix;

            if (counter.Count(candidate) <= limit)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var cut = low;
        var newline = normalized.LastIndexOf('\n', Math.Max(0, cut - 1));
        if (newline > normalized.Length / 3)
        {
            cut = newline;
        }

        return string.Concat(normalized.AsSpan(0, cut).TrimEnd(), suffix);
    }

    /// <summary>摘要是否来自确定性兜底（开头带标记）。</summary>
    public static bool IsFallback(string? text) =>
        text is not null && text.TrimStart().StartsWith(FallbackMarker, StringComparison.Ordinal);

    /// <summary>
    /// 把「第几条消息」写的 <c>[mN]</c> 换成会话里的绝对序号（P2b §3.3.2 第二条）。
    /// <para>
    /// 热前缀回放要求被遮蔽区域**逐字**发给模型，所以不能往每条消息前面插 <c>[mN]</c> 标记
    /// （插了就与上次请求不再逐字节相同，缓存前缀在第一条消息处断掉）。折中办法是让模型按位置数
    /// （"第一条是 m1"），再由我们把位置换回绝对序号。
    /// </para>
    /// <para>
    /// 超出本批范围的值原样保留：那通常是上一版摘要里的绝对序号，或模型照抄的来源。
    /// </para>
    /// </summary>
    public static string RemapSources(string? text, int chunkStartOrder, int chunkCount)
    {
        if (string.IsNullOrEmpty(text) || chunkCount <= 0)
        {
            return text ?? string.Empty;
        }

        return SourcePattern.Replace(text, match =>
        {
            if (!int.TryParse(match.Groups["order"].Value, out var ordinal) || ordinal < 1 || ordinal > chunkCount)
            {
                return match.Value;
            }

            return $"[m{chunkStartOrder + ordinal - 1}]";
        });
    }

    /// <summary>从一条消息里提取来源序号（测试 / 调试用）。</summary>
    public static IReadOnlyList<int> Sources(string text) =>
        SourcePattern.Matches(text)
            .Select(match => int.TryParse(match.Groups["order"].Value, out var order) ? order : -1)
            .Where(order => order >= 0)
            .Distinct()
            .Order()
            .ToList();

    private static string Truncate(string text, int maxChars) => text.Length <= maxChars
        ? text
        : string.Concat(text.AsSpan(0, maxChars), "…");
}
