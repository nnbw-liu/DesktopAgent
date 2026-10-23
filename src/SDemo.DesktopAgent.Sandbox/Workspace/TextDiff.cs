using System.Text;

namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// 轻量统一 diff（行级 LCS）。为避免大文件把内存/CPU 吃满，超过 1200 行只给摘要；
/// 输出最多 <c>maxLines</c> 行。只用于展示，不参与写回。
/// </summary>
internal static class TextDiff
{
    private const int MaxLcsLines = 1200;
    private const int ContextLines = 3;

    private enum OpKind
    {
        Equal,
        Delete,
        Insert
    }

    private sealed record Op(OpKind Kind, string Line, int OldLine, int NewLine);

    public static string? Create(string relativePath, string oldText, string newText, int maxLines)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        if (oldLines.Length > MaxLcsLines || newLines.Length > MaxLcsLines)
        {
            return $"文件较大，仅记录变更摘要：+{newLines.Length} / -{oldLines.Length} 行。";
        }

        var operations = BuildOperations(oldLines, newLines);
        var hunks = BuildHunks(operations);
        if (hunks.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("--- a/").Append(relativePath).Append('\n');
        builder.Append("+++ b/").Append(relativePath).Append('\n');

        foreach (var (start, end) in hunks)
        {
            var oldStart = operations[start].OldLine;
            var newStart = operations[start].NewLine;
            var oldCount = operations.Skip(start).Take(end - start).Count(op => op.Kind != OpKind.Insert);
            var newCount = operations.Skip(start).Take(end - start).Count(op => op.Kind != OpKind.Delete);
            oldCount = Math.Max(oldCount, 1);
            newCount = Math.Max(newCount, 1);

            builder.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
                .Append(" +").Append(newStart).Append(',').Append(newCount)
                .Append(" @@\n");

            for (var index = start; index < end; index++)
            {
                var op = operations[index];
                builder.Append(op.Kind switch
                {
                    OpKind.Equal => ' ',
                    OpKind.Delete => '-',
                    _ => '+'
                }).Append(op.Line).Append('\n');
            }
        }

        var lines = builder.ToString().Split('\n');
        if (lines.Length > maxLines)
        {
            return string.Join('\n', lines.Take(maxLines)) + "\n…（diff 已截断）";
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static List<Op> BuildOperations(string[] oldLines, string[] newLines)
    {
        var rows = oldLines.Length;
        var columns = newLines.Length;
        var lcs = new int[rows + 1, columns + 1];

        for (var i = rows - 1; i >= 0; i--)
        {
            for (var j = columns - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var operations = new List<Op>(rows + columns);
        var oldIndex = 0;
        var newIndex = 0;
        var oldLine = 1;
        var newLine = 1;

        while (oldIndex < rows || newIndex < columns)
        {
            if (oldIndex < rows && newIndex < columns
                && string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal))
            {
                operations.Add(new Op(OpKind.Equal, oldLines[oldIndex], oldLine++, newLine++));
                oldIndex++;
                newIndex++;
                continue;
            }

            if (newIndex < columns
                && (oldIndex >= rows || lcs[oldIndex, newIndex + 1] >= lcs[oldIndex + 1, newIndex]))
            {
                operations.Add(new Op(OpKind.Insert, newLines[newIndex], oldLine, newLine++));
                newIndex++;
                continue;
            }

            if (oldIndex < rows)
            {
                operations.Add(new Op(OpKind.Delete, oldLines[oldIndex], oldLine++, newLine));
                oldIndex++;
            }
        }

        return operations;
    }

    private static List<(int Start, int End)> BuildHunks(List<Op> operations)
    {
        var hunks = new List<(int Start, int End)>();
        int? start = null;
        var lastChange = -1;

        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Kind == OpKind.Equal)
            {
                continue;
            }

            if (start is null)
            {
                start = Math.Max(0, index - ContextLines);
                lastChange = index;
                continue;
            }

            if (index - lastChange > ContextLines * 2)
            {
                hunks.Add((start.Value, Math.Min(operations.Count, lastChange + ContextLines + 1)));
                start = Math.Max(0, index - ContextLines);
            }

            lastChange = index;
        }

        if (start is not null && lastChange >= start)
        {
            hunks.Add((start.Value, Math.Min(operations.Count, lastChange + ContextLines + 1)));
        }

        return hunks;
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
