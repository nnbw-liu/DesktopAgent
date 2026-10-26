using System.Text;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 技能渐进披露（M2 §4.5）：系统提示只放索引，正文由 <c>load_skill</c> 按需读取。
/// 索引按关键词挑 top N，渲染成紧凑的「名字：一句话（何时用）」列表。
/// </summary>
public static class SkillIndexBuilder
{
    /// <summary>默认只放 3–5 条；技能很多时也不撑爆技能段。</summary>
    public const int DefaultMaxEntries = 5;

    /// <summary>按用户输入挑出最相关的技能；命中不足时按文件名为序补满。</summary>
    /// <remarks><c>disable-model-invocation</c> 的技能不进索引：模型看不到，只有 `/name` 能加载。</remarks>
    public static IReadOnlyList<SkillIndexEntry> Select(
        IReadOnlyList<SkillIndexEntry> entries,
        string? userInput,
        int maxEntries = DefaultMaxEntries)
    {
        entries = entries.Where(entry => !entry.DisableModelInvocation).ToArray();

        if (entries.Count == 0)
        {
            return [];
        }

        var limit = Math.Clamp(maxEntries, 1, 20);
        if (entries.Count <= limit)
        {
            return entries;
        }

        var tokens = Tokenize(userInput);
        if (tokens.Length == 0)
        {
            return entries.Take(limit).ToArray();
        }

        var ranked = entries
            .Select((entry, index) => (Entry: entry, Score: Score(entry, tokens), Index: index))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(limit)
            .Select(item => item.Entry)
            .ToList();

        // 关键词一个都没命中时，ranked 依然是文件顺序的前 N 条，不需要再补。
        return ranked;
    }

    /// <summary>
    /// 渲染索引块。返回空字符串表示没有技能；<paramref name="selectedSkill"/> 会被标注，
    /// 因为它的全文已经由系统提示注入，模型不需要再 load_skill。
    /// </summary>
    public static string Render(IReadOnlyList<SkillIndexEntry> entries, string? selectedSkill = null)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("\n\n## 可用技能（索引）\n");
        builder.Append("需要正文时调用 load_skill(name)；目录包里的 references 用 read_skill_file(name, path)。不要凭名字猜内容。\n");

        foreach (var entry in entries)
        {
            builder.Append("- ").Append(entry.Name).Append('：').Append(entry.Description);

            var whenToUse = entry.WhenToUse;
            if (!string.IsNullOrWhiteSpace(whenToUse)
                && !string.Equals(whenToUse, entry.Description, StringComparison.Ordinal))
            {
                builder.Append("（何时用：").Append(whenToUse).Append('）');
            }

            var tags = entry.Tags.Take(3).ToArray();
            if (tags.Length > 0)
            {
                builder.Append(" [").Append(string.Join('/', tags)).Append(']');
            }

            if (!string.IsNullOrEmpty(selectedSkill)
                && string.Equals(entry.Name, selectedSkill, StringComparison.Ordinal))
            {
                builder.Append("（本轮已手选，全文已在下文注入）");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>按空白 / 标点切词；中文长串再补 2-gram，避免整句匹配不到「总结」这种短标签。</summary>
    internal static string[] Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in input.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                Flush();
            }
            else
            {
                current.Append(character);
            }
        }

        Flush();
        return tokens.Distinct(StringComparer.Ordinal).ToArray();

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var token = current.ToString();
            tokens.Add(token);

            if (token.Length > 2)
            {
                for (var i = 0; i + 2 <= token.Length; i++)
                {
                    tokens.Add(token.Substring(i, 2));
                }
            }

            current.Clear();
        }
    }

    private static int Score(SkillIndexEntry entry, IReadOnlyList<string> tokens)
    {
        var score = 0;

        foreach (var token in tokens)
        {
            if (token.Length < 2)
            {
                continue;
            }

            if (entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (entry.Description.Contains(token, StringComparison.OrdinalIgnoreCase)
                || entry.WhenToUse.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (entry.Tags.Any(tag => tag.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2;
            }
        }

        return score;
    }
}
