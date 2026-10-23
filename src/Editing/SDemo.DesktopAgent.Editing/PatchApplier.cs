using System.Text;

namespace SDemo.DesktopAgent.Editing;

/// <summary>单个文件的补丁结果。</summary>
public sealed record PatchFileChange(
    string Path,
    bool Created,
    int Added,
    int Removed,
    string NewHash);

/// <summary>补丁应用结果；失败时 <see cref="Message"/> 包含可供模型修正的上下文。</summary>
public sealed record PatchResult(
    bool Success,
    string Message,
    IReadOnlyList<PatchFileChange> Changes,
    string? Error = null);

/// <summary>
/// 应用 unified diff（P3 §3.4）：先解析 / 校验 / 计算全部文件的新内容，全部成功才落盘；
/// 保留原换行风格与末尾换行状态；写文件用临时文件 + 原子替换；失败回滚已写入的文件。
/// </summary>
public sealed class PatchApplier
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public PatchResult Apply(
        string workspaceRoot,
        ParsedPatch patch,
        string? expectedHash = null,
        CancellationToken cancellationToken = default)
    {
        var pending = new List<PendingWrite>();
        var errors = new List<string>();

        try
        {
            foreach (var file in patch.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = WorkspacePath.Resolve(workspaceRoot, file.NewPath, allowRoot: false);
                var exists = File.Exists(target);
                var originalText = exists ? File.ReadAllText(target) : string.Empty;
                var originalHash = exists ? FileReadRegistry.HashFile(target) : null;

                if (file.IsNew && exists)
                {
                    errors.Add($"{file.NewPath}：补丁把它当新文件，但文件已存在。");
                    continue;
                }

                if (!file.IsNew && !exists)
                {
                    errors.Add($"{file.NewPath}：文件不存在，不能用修改补丁。");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(expectedHash)
                    && !file.IsNew
                    && (!exists || !string.Equals(originalHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{file.NewPath}：文件已被改动（hash 不匹配），请重新 read_file 后再试。");
                    continue;
                }

                var applied = TryApplyFile(file, originalText, out var newText, out var added, out var removed, out var reason);
                if (!applied)
                {
                    errors.Add(reason);
                    continue;
                }

                pending.Add(new PendingWrite(
                    file.NewPath,
                    target,
                    exists,
                    originalText,
                    newText,
                    added,
                    removed,
                    FileReadRegistry.HashText(newText)));
            }

            if (errors.Count > 0)
            {
                return new PatchResult(false, "未写入任何文件：\n" + string.Join('\n', errors), [], string.Join('\n', errors));
            }

            if (pending.Count == 0)
            {
                return new PatchResult(false, "未写入任何文件：补丁没有产生任何改动。", [], "没有改动");
            }

            var written = new List<PendingWrite>();

            try
            {
                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteAtomically(item.Target, item.NewText);
                    written.Add(item);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Rollback(written);
                return new PatchResult(
                    false,
                    $"未写入任何文件：写盘失败（{ex.Message}），已回滚。",
                    [],
                    ex.Message);
            }

            var changes = pending
                .Select(item => new PatchFileChange(item.RelativePath, !item.Existed, item.Added, item.Removed, item.NewHash))
                .ToArray();

            return new PatchResult(true, FormatSuccess(changes), changes);
        }
        catch (InvalidOperationException ex)
        {
            return new PatchResult(false, $"未写入任何文件：{ex.Message}", [], ex.Message);
        }
    }

    /// <summary>生成给模型和审计用的成功文本；末尾的 <c>[EDIT]</c> 行由聊天层解析成改动摘要。</summary>
    public static string FormatSuccess(IReadOnlyList<PatchFileChange> changes)
    {
        var builder = new StringBuilder();
        builder.Append($"已修改 {changes.Count} 个文件：");

        var parts = changes
            .Select(change => $"{change.Path} (+{change.Added} -{change.Removed}){(change.Created ? "（新建）" : string.Empty)}")
            .ToArray();

        builder.AppendJoin('、', parts);

        foreach (var change in changes)
        {
            builder.Append('\n')
                .Append("[EDIT] ")
                .Append(change.Path)
                .Append(" +")
                .Append(change.Added)
                .Append(" -")
                .Append(change.Removed);
        }

        return builder.ToString();
    }

    private static bool TryApplyFile(
        PatchFile file,
        string originalText,
        out string newText,
        out int added,
        out int removed,
        out string reason)
    {
        added = 0;
        removed = 0;
        newText = originalText;
        reason = string.Empty;

        var source = TextContent.Parse(originalText);
        var lines = new List<string>(source.Lines);
        var cursor = 0;
        var lastEnd = 0;

        for (var hunkIndex = 0; hunkIndex < file.Hunks.Count; hunkIndex++)
        {
            var hunk = file.Hunks[hunkIndex];
            var oldLines = hunk.Lines.Where(line => line.Kind is ' ' or '-').Select(line => line.Text).ToArray();
            var newLines = hunk.Lines.Where(line => line.Kind is ' ' or '+').Select(line => line.Text).ToArray();

            var expected = file.IsNew
                ? cursor
                : Math.Clamp(hunk.OldStart - 1 + (cursor - lastEnd), 0, lines.Count);
            expected = Math.Clamp(expected, cursor, lines.Count);

            int position;
            if (oldLines.Length == 0)
            {
                position = expected;
            }
            else
            {
                (position, _) = FindBlock(lines, oldLines, expected);
                if (position < 0)
                {
                    reason = $"{file.NewPath}：第 {hunkIndex + 1} 个 hunk 未匹配。\n"
                        + BuildFailureContext(lines, oldLines, expected)
                        + BuildCandidatePatch(file, hunk, lines, expected);
                    newText = originalText;
                    return false;
                }
            }

            lines.RemoveRange(position, oldLines.Length);
            lines.InsertRange(position, newLines);

            lastEnd = position + oldLines.Length;
            cursor = position + newLines.Length;
            added += hunk.Lines.Count(line => line.Kind == '+');
            removed += hunk.Lines.Count(line => line.Kind == '-');
        }

        var noFinalNewline = file.Hunks
            .SelectMany(hunk => hunk.Lines)
            .LastOrDefault()?.NoNewline == true;

        var finalNewline = file.IsNew
            ? !noFinalNewline
            : noFinalNewline ? false : source.FinalNewline;

        newText = source.Join(lines, finalNewline);
        return true;
    }

    /// <summary>
    /// 精确 → 忽略行尾空白 → 缩进容错（P1）。缩进容错只在块至少两行、且相似度 ≥70% 时启用，
    /// 并选离 hunk 头最近的位置，避免把单行改动落到同名行上。
    /// </summary>
    private static (int Index, bool Fuzzy) FindBlock(
        IReadOnlyList<string> source,
        IReadOnlyList<string> block,
        int expected)
    {
        var exact = Find(source, block, expected, static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
        if (exact >= 0)
        {
            return (exact, false);
        }

        var trailing = Find(source, block, expected, static (left, right) =>
            string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal));
        if (trailing >= 0)
        {
            return (trailing, false);
        }

        if (block.Count < 2 || block.All(string.IsNullOrWhiteSpace))
        {
            return (-1, false);
        }

        var fuzzy = FindFuzzy(source, block, expected, out var score);
        return fuzzy >= 0 && score >= 0.7 ? (fuzzy, true) : (-1, false);
    }

    /// <summary>
    /// 逐行打分定位：精确 3 分、忽略行尾空白 2 分、忽略首尾空白 1 分；
    /// 相似度达到 80% 才接受，同分时选离期望位置最近的窗口。
    /// </summary>
    private static int FindFuzzy(
        IReadOnlyList<string> source,
        IReadOnlyList<string> block,
        int expected,
        out double score)
    {
        var best = -1;
        var bestScore = 0.0;
        var bestDistance = int.MaxValue;
        var maximum = block.Count * 3.0;
        var scan = Math.Min(source.Count - block.Count + 1, 20_000);

        for (var index = 0; index < scan; index++)
        {
            var points = 0;
            for (var offset = 0; offset < block.Count; offset++)
            {
                var left = source[index + offset];
                var right = block[offset];

                if (string.Equals(left, right, StringComparison.Ordinal))
                {
                    points += 3;
                }
                else if (string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal))
                {
                    points += 2;
                }
                else if (string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal))
                {
                    points += 1;
                }
            }

            var ratio = points / maximum;
            var distance = Math.Abs(index - expected);

            if (ratio > bestScore || (Math.Abs(ratio - bestScore) < 0.0001 && distance < bestDistance))
            {
                bestScore = ratio;
                bestDistance = distance;
                best = index;
            }
        }

        score = bestScore;
        return best;
    }

    private static int Find(
        IReadOnlyList<string> source,
        IReadOnlyList<string> block,
        int expected,
        Func<string, string, bool> equals)
    {
        if (block.Count == 0)
        {
            return expected;
        }

        for (var index = expected; index + block.Count <= source.Count; index++)
        {
            if (Matches(source, block, index, equals))
            {
                return index;
            }
        }

        for (var index = 0; index < expected && index + block.Count <= source.Count; index++)
        {
            if (Matches(source, block, index, equals))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Matches(
        IReadOnlyList<string> source,
        IReadOnlyList<string> block,
        int index,
        Func<string, string, bool> equals)
    {
        for (var offset = 0; offset < block.Count; offset++)
        {
            if (!equals(source[index + offset], block[offset]))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildFailureContext(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> expected,
        int expectedIndex)
    {
        var window = Math.Max(1, expected.Count);
        var best = FindBestWindow(lines, expected);
        var bestIndex = best.Index >= 0
            ? best.Index
            : Math.Clamp(expectedIndex, 0, Math.Max(0, lines.Count - 1));

        var from = Math.Max(0, bestIndex - 5);
        var to = Math.Min(lines.Count - 1, bestIndex + window + 5);
        var builder = new StringBuilder();
        builder.Append($"最近相似的片段（第 {from + 1}–{to + 1} 行）：");

        for (var index = from; index <= to; index++)
        {
            builder.Append('\n').Append(index + 1).Append("| ").Append(lines[index]);
        }

        builder.Append("\n建议先 read_file 查看当前内容，再按新内容生成补丁。");
        return builder.ToString();
    }

    private static (int Index, double Score) FindBestWindow(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> expected)
    {
        if (expected.Count == 0 || lines.Count < expected.Count)
        {
            return (-1, 0);
        }

        var window = expected.Count;
        var scan = Math.Min(lines.Count - window + 1, 5000);
        var bestIndex = -1;
        var bestScore = 0.0;
        var maximum = window * 2.0;

        for (var index = 0; index < scan; index++)
        {
            var score = 0;
            for (var offset = 0; offset < window; offset++)
            {
                if (string.Equals(lines[index + offset], expected[offset], StringComparison.Ordinal))
                {
                    score += 2;
                }
                else if (string.Equals(lines[index + offset].TrimEnd(), expected[offset].TrimEnd(), StringComparison.Ordinal))
                {
                    score += 1;
                }
            }

            var ratio = score / maximum;
            if (ratio > bestScore)
            {
                bestScore = ratio;
                bestIndex = index;
            }
        }

        return (bestIndex, bestScore);
    }

    /// <summary>
    /// P1：失败时给一份“可复制”的候选补丁。上下文行用文件里的实际内容重写，行号按最近窗口，
    /// 删除行仍保留模型写的内容；它不保证一定可应用，目的是把“盲试”变成“改一处再提交”。
    /// </summary>
    private static string BuildCandidatePatch(
        PatchFile file,
        PatchHunk hunk,
        IReadOnlyList<string> lines,
        int expectedIndex)
    {
        var oldLines = hunk.Lines.Where(line => line.Kind is ' ' or '-').Select(line => line.Text).ToArray();
        var best = FindBestWindow(lines, oldLines);
        var start = best.Index >= 0
            ? best.Index
            : Math.Clamp(expectedIndex, 0, Math.Max(0, lines.Count - 1));

        var oldCount = hunk.Lines.Count(line => line.Kind is ' ' or '-');
        var newCount = hunk.Lines.Count(line => line.Kind is ' ' or '+');
        var builder = new StringBuilder();
        builder.Append("\n\n候选补丁（上下文已按文件第 ")
            .Append(start + 1)
            .Append(" 行附近重写；请核对删除行后直接提交）：\n")
            .Append("--- a/").Append(file.NewPath).Append('\n')
            .Append("+++ b/").Append(file.NewPath).Append('\n')
            .Append("@@ -").Append(start + 1).Append(',').Append(Math.Max(1, oldCount))
            .Append(" +").Append(start + 1).Append(',').Append(Math.Max(1, newCount))
            .Append(" @@\n");

        var actualIndex = start;
        foreach (var line in hunk.Lines)
        {
            switch (line.Kind)
            {
                case '+':
                    builder.Append('+').Append(line.Text).Append('\n');
                    break;

                case '-':
                    // 删除行也用实际内容重写：模型写错锚点时，候选补丁仍然能应用。
                    var removed = actualIndex >= 0 && actualIndex < lines.Count
                        ? lines[actualIndex]
                        : line.Text;
                    builder.Append('-').Append(removed).Append('\n');
                    actualIndex++;
                    break;

                default:
                    var actual = actualIndex >= 0 && actualIndex < lines.Count
                        ? lines[actualIndex]
                        : line.Text;
                    builder.Append(' ').Append(actual).Append('\n');
                    actualIndex++;
                    break;
            }

            if (builder.Length > 6000)
            {
                builder.Append("…（候选补丁过长，已截断）\n");
                return builder.ToString();
            }
        }

        return builder.ToString();
    }

    private static void WriteAtomically(string target, string content)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{target}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, content, Utf8NoBom);
        File.Move(temporary, target, overwrite: true);
    }

    private static void Rollback(IReadOnlyList<PendingWrite> written)
    {
        foreach (var item in written.Reverse())
        {
            try
            {
                if (item.Existed)
                {
                    File.WriteAllText(item.Target, item.OriginalText, Utf8NoBom);
                }
                else if (File.Exists(item.Target))
                {
                    File.Delete(item.Target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 回滚尽力而为；错误已经在上层返回。
            }
        }
    }

    private sealed record PendingWrite(
        string RelativePath,
        string Target,
        bool Existed,
        string OriginalText,
        string NewText,
        int Added,
        int Removed,
        string NewHash);

   private sealed record TextContent(IReadOnlyList<string> Lines, string Newline, bool FinalNewline)
    {
        public static TextContent Parse(string text)
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var finalNewline = normalized.EndsWith('\n');
            var body = finalNewline ? normalized[..^1] : normalized;
            var lines = body.Length == 0 ? [] : body.Split('\n');
            return new TextContent(lines, newline, finalNewline);
        }

        public string Join(IReadOnlyList<string> lines, bool finalNewline)
        {
            var text = string.Join(Newline, lines);
            return finalNewline ? text + Newline : text;
        }
    }
}
