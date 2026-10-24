namespace SDemo.DesktopAgent.Editing;

/// <summary>diff 里一行属于哪种类型。</summary>
public enum DiffLineKind
{
    /// <summary>未改动的上下文行。</summary>
    Context,

    /// <summary>新增行（<c>+</c>）。</summary>
    Added,

    /// <summary>删除行（<c>-</c>）。</summary>
    Removed,

    /// <summary>hunk 头（<c>@@ -a,b +c,d @@</c>）。</summary>
    Hunk
}

/// <summary>diff 的一行：文本 + 两侧行号（hunk 头没有行号）。</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>一个文件的改动预览；<see cref="Note"/> 用来解释「为什么没有 diff」。</summary>
public sealed record FileDiff(
    bool IsBinary,
    bool Truncated,
    int Added,
    int Removed,
    IReadOnlyList<DiffLine> Lines,
    string? Note)
{
    public static readonly FileDiff Empty = new(false, false, 0, 0, [], null);

    public bool HasChanges => Added > 0 || Removed > 0;

    /// <summary>扁平化成可直接放进 &lt;pre&gt; 或复制的统一 diff 文本。</summary>
    public string ToText()
    {
        if (Lines.Count == 0)
        {
            return Note ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder();

        foreach (var line in Lines)
        {
            builder.Append(line.Kind switch
            {
                DiffLineKind.Added => '+',
                DiffLineKind.Removed => '-',
                DiffLineKind.Hunk => ' ',
                _ => ' '
            });

            builder.Append(line.Text).Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>
/// 行级统一 diff（P3 界面预览用）：先剪掉公共前后缀，只在中间跑 LCS，
/// 并把结果压成带 3 行上下文的 hunk。大文件只标记截断，不做全量比较。
/// </summary>
public static class FileDiffBuilder
{
    /// <summary>中间段最多多少行才做 LCS（两侧相加，超过就退化成「全删再全加」）。</summary>
    public const int MaxComparableLines = 1200;

    /// <summary>输出行数上限，避免一个几千行的文件把界面拖垮。</summary>
    public const int MaxOutputLines = 600;

    /// <summary>hunk 前后保留的上下文行数。</summary>
    public const int ContextLines = 3;

    private const string BinaryNote = "二进制文件，不显示文本差异。";
    private const string TooLargeNote = "文件较大，只显示差异的截断预览。";

    public static FileDiff Build(string? before, string? after)
    {
        before ??= string.Empty;
        after ??= string.Empty;

        if (IsBinary(before) || IsBinary(after))
        {
            return new FileDiff(true, false, 0, 0, [], BinaryNote);
        }

        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);

        if (oldLines.SequenceEqual(newLines, StringComparer.Ordinal))
        {
            return FileDiff.Empty;
        }

        // 公共前后缀不计入 LCS：真实编辑通常只占文件的一小段。
        var prefix = 0;
        while (prefix < oldLines.Count
               && prefix < newLines.Count
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix
               && suffix < newLines.Count - prefix
               && string.Equals(oldLines[oldLines.Count - 1 - suffix], newLines[newLines.Count - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var oldMiddle = Slice(oldLines, prefix, oldLines.Count - suffix);
        var newMiddle = Slice(newLines, prefix, newLines.Count - suffix);

        var truncated = false;
        List<(DiffLineKind Kind, string Text)> middle;

        if (oldMiddle.Count + newMiddle.Count > MaxComparableLines)
        {
            // 改动太大：不跑 LCS，直接整段替换（仍然给出可用的 +/− 预览）。
            truncated = true;
            middle = [];
            middle.AddRange(oldMiddle.Select(line => (DiffLineKind.Removed, line)));
            middle.AddRange(newMiddle.Select(line => (DiffLineKind.Added, line)));
        }
        else
        {
            middle = Diff(oldMiddle, newMiddle);
        }

        var added = middle.Count(op => op.Kind == DiffLineKind.Added);
        var removed = middle.Count(op => op.Kind == DiffLineKind.Removed);

        // hunk 要带上改动前后的几行上下文，否则读者看不出改在哪儿。
        var leading = Math.Min(prefix, ContextLines);
        var trailing = Math.Min(suffix, ContextLines);

        var ops = new List<(DiffLineKind Kind, string Text)>(middle.Count + leading + trailing);

        for (var index = prefix - leading; index < prefix; index++)
        {
            ops.Add((DiffLineKind.Context, oldLines[index]));
        }

        ops.AddRange(middle);

        var suffixStart = oldLines.Count - suffix;
        for (var index = 0; index < trailing; index++)
        {
            ops.Add((DiffLineKind.Context, oldLines[suffixStart + index]));
        }

        var lines = BuildHunks(prefix - leading, ops, ref truncated);

        return new FileDiff(
            false,
            truncated,
            added,
            removed,
            lines,
            truncated ? TooLargeNote : null);
    }

    /// <summary>把操作序列还原成带行号、带上下文的 hunk 列表。</summary>
    private static List<DiffLine> BuildHunks(
        int startIndex,
        IReadOnlyList<(DiffLineKind Kind, string Text)> middle,
        ref bool truncated)
    {
        // 先算出每一行对应的旧 / 新行号，再按「变化点 ± ContextLines」切 hunk。
        var numbered = new List<DiffLine>(middle.Count);
        var oldIndex = startIndex;
        var newIndex = startIndex;

        foreach (var (kind, text) in middle)
        {
            switch (kind)
            {
                case DiffLineKind.Removed:
                    numbered.Add(new DiffLine(kind, text, oldIndex + 1, null));
                    oldIndex++;
                    break;
                case DiffLineKind.Added:
                    numbered.Add(new DiffLine(kind, text, null, newIndex + 1));
                    newIndex++;
                    break;
                default:
                    numbered.Add(new DiffLine(kind, text, oldIndex + 1, newIndex + 1));
                    oldIndex++;
                    newIndex++;
                    break;
            }
        }

        var changes = new List<int>();
        for (var index = 0; index < numbered.Count; index++)
        {
            if (numbered[index].Kind != DiffLineKind.Context)
            {
                changes.Add(index);
            }
        }

        if (changes.Count == 0)
        {
            return [];
        }

        // 变化点之间不超过 2 * ContextLines 行就并进同一个 hunk。
        var clusters = new List<(int Start, int End)>();
        var clusterStart = changes[0];
        var clusterEnd = changes[0];

        foreach (var change in changes.Skip(1))
        {
            if (change - clusterEnd > ContextLines * 2)
            {
                clusters.Add((clusterStart, clusterEnd));
                clusterStart = change;
            }

            clusterEnd = change;
        }

        clusters.Add((clusterStart, clusterEnd));

        var result = new List<DiffLine>(Math.Min(MaxOutputLines, numbered.Count + clusters.Count * 2));

        foreach (var (start, end) in clusters)
        {
            var from = Math.Max(0, start - ContextLines);
            var to = Math.Min(numbered.Count, end + ContextLines + 1);
            var block = numbered.GetRange(from, to - from);

            var oldStart = block.FirstOrDefault(line => line.OldLine is not null)?.OldLine;
            var newStart = block.FirstOrDefault(line => line.NewLine is not null)?.NewLine;
            var oldCount = block.Count(line => line.OldLine is not null);
            var newCount = block.Count(line => line.NewLine is not null);

            if (oldStart is null || newStart is null)
            {
                // 纯新增 / 纯删除的边界：用相邻行号补一个稳定的起点。
                oldStart ??= startIndex + 1;
                newStart ??= startIndex + 1;
            }

            result.Add(new DiffLine(
                DiffLineKind.Hunk,
                $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@",
                null,
                null));

            foreach (var line in block)
            {
                if (result.Count >= MaxOutputLines)
                {
                    truncated = true;
                    break;
                }

                result.Add(line);
            }

            if (truncated)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>经典 LCS 动态规划；只返回中间段（公共前后缀已剪掉）。</summary>
    private static List<(DiffLineKind Kind, string Text)> Diff(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines)
    {
        var rows = oldLines.Count + 1;
        var columns = newLines.Count + 1;
        var table = new int[rows * columns];

        for (var i = oldLines.Count - 1; i >= 0; i--)
        {
            for (var j = newLines.Count - 1; j >= 0; j--)
            {
                table[i * columns + j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? table[(i + 1) * columns + j + 1] + 1
                    : Math.Max(table[(i + 1) * columns + j], table[i * columns + j + 1]);
            }
        }

        var ops = new List<(DiffLineKind Kind, string Text)>(oldLines.Count + newLines.Count);
        var x = 0;
        var y = 0;

        while (x < oldLines.Count && y < newLines.Count)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                ops.Add((DiffLineKind.Context, oldLines[x]));
                x++;
                y++;
            }
            else if (table[(x + 1) * columns + y] >= table[x * columns + y + 1])
            {
                ops.Add((DiffLineKind.Removed, oldLines[x]));
                x++;
            }
            else
            {
                ops.Add((DiffLineKind.Added, newLines[y]));
                y++;
            }
        }

        while (x < oldLines.Count)
        {
            ops.Add((DiffLineKind.Removed, oldLines[x]));
            x++;
        }

        while (y < newLines.Count)
        {
            ops.Add((DiffLineKind.Added, newLines[y]));
            y++;
        }

        return ops;
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var parts = normalized.Split('\n').ToList();

        // 末尾换行不算额外一行。
        if (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    private static List<string> Slice(IReadOnlyList<string> lines, int start, int end)
    {
        var result = new List<string>(Math.Max(0, end - start));

        for (var index = start; index < end; index++)
        {
            result.Add(lines[index]);
        }

        return result;
    }

    private static bool IsBinary(string text) => text.Contains('\0');
}
