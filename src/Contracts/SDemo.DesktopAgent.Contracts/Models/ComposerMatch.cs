namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 补全菜单的候选排序：与 DSH 的 <c>/</c> 菜单同一套规则——大小写不敏感的**有序子序列**匹配，
/// 前缀命中排最前，其次按匹配长度、再按候选长度，最后保持宿主顺序。
/// </summary>
public static class ComposerMatch
{
    /// <summary>从候选里筛出匹配 query 的前 <paramref name="max"/> 条；query 为空时按原顺序截断。</summary>
    public static IReadOnlyList<T> Rank<T>(
        IEnumerable<T> items,
        Func<T, string> key,
        string? query,
        int max)
    {
        if (max <= 0)
        {
            return [];
        }

        var trimmed = (query ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return items.Take(max).ToArray();
        }

        var scored = new List<(T Item, int Score, int Index, int Length)>();
        var index = 0;

        foreach (var item in items)
        {
            var text = key(item);
            var score = Score(text, trimmed);

            if (score >= 0)
            {
                scored.Add((item, score, index, text.Length));
            }

            index++;
        }

        return scored
            .OrderBy(entry => entry.Score)
            .ThenBy(entry => entry.Length)
            .ThenBy(entry => entry.Index)
            .Take(max)
            .Select(entry => entry.Item)
            .ToArray();
    }

    /// <summary>有序子序列匹配；命中返回排序分（越小越靠前），不命中返回 -1。</summary>
    public static int Score(string? candidate, string query)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return -1;
        }

        if (query.Length == 0)
        {
            return 0;
        }

        var queryIndex = 0;
        var firstHit = -1;
        var gaps = 0;
        var lastHit = -1;

        for (var i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            if (char.ToUpperInvariant(candidate[i]) != char.ToUpperInvariant(query[queryIndex]))
            {
                continue;
            }

            if (firstHit < 0)
            {
                firstHit = i;
            }

            if (lastHit >= 0)
            {
                gaps += i - lastHit - 1;
            }

            lastHit = i;
            queryIndex++;
        }

        if (queryIndex < query.Length)
        {
            return -1;
        }

        // 0xxx：前缀命中（@ 后面的整段以 query 开头 / 路径某一段以 query 开头）
        var segmentStart = firstHit == 0
            || candidate[firstHit - 1] is '/' or '\\' or ' ' or '.' or '-';

        var prefix = segmentStart && candidate.Length > firstHit
            && string.Compare(candidate, firstHit, query, 0, query.Length, StringComparison.OrdinalIgnoreCase) == 0;

        return (prefix ? 0 : 1000) + gaps + firstHit;
    }
}
