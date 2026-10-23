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
                position = FindBlock(lines, oldLines, expected);
                if (position < 0)
                {
                    reason = $"{file.NewPath}：第 {hunkIndex + 1} 个 hunk 未匹配。\n"
                        + BuildFailureContext(lines, oldLines, expected);
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

    /// <summary>精确 → 忽略行尾空白 → 从期望位置向后搜索；返回 -1 表示未找到。</summary>
    private static int FindBlock(IReadOnlyList<string> source, IReadOnlyList<string> block, int expected)
    {
        var exact = Find(source, block, expected, static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
        if (exact >= 0)
        {
            return exact;
        }

        return Find(source, block, expected, static (left, right) =>
            string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal));
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
        var bestIndex = -1;
        var bestScore = -1;

        // 找最像的窗口，给模型一个可对照的片段；最多扫 5000 个窗口，避免大文件卡住。
        var window = Math.Max(1, expected.Count);
        var scan = Math.Min(Math.Max(0, lines.Count - window + 1), 5000);

        for (var index = 0; index < scan; index++)
        {
            var score = 0;
            for (var offset = 0; offset < window; offset++)
            {
                var left = lines[index + offset];
                var right = expected[offset];

                if (string.Equals(left, right, StringComparison.Ordinal))
                {
                    score += 2;
                }
                else if (string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal))
                {
                    score += 1;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        if (bestIndex < 0)
        {
            bestIndex = Math.Clamp(expectedIndex, 0, Math.Max(0, lines.Count - 1));
        }

        var from = Math.Max(0, bestIndex - 5);
        var to = Math.Min(lines.Count - 1, bestIndex + Math.Max(window, 1) + 5);
        var builder = new StringBuilder();
        builder.Append($"最近相似的片段（第 {from + 1}–{to + 1} 行）：");

        for (var index = from; index <= to; index++)
        {
            builder.Append('\n').Append(index + 1).Append("| ").Append(lines[index]);
        }

        builder.Append("\n建议先 read_file 查看当前内容，再按新内容生成补丁。");
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
