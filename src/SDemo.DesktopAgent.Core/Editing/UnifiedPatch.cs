using System.Text;
using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Editing;

/// <summary>diff 里的一行：<c> </c> 上下文、<c>+</c> 新增、<c>-</c> 删除。</summary>
public sealed record PatchLine(char Kind, string Text, bool NoNewline = false);

/// <summary>一个 hunk。行号来自 diff 头，应用时允许位置漂移后再按内容定位。</summary>
public sealed record PatchHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<PatchLine> Lines);

/// <summary>一个文件的补丁；<see cref="IsNew"/> 表示 <c>--- /dev/null</c>。</summary>
public sealed record PatchFile(
    string? OldPath,
    string NewPath,
    bool IsNew,
    IReadOnlyList<PatchHunk> Hunks);

/// <summary>解析后的多文件补丁。</summary>
public sealed record ParsedPatch(IReadOnlyList<PatchFile> Files, string Raw);

/// <summary>
/// 标准 unified diff 解析器（P3 §3.4）。只接受相对路径，拒绝绝对路径 / UNC / <c>..</c>；
/// 多文件、多 hunk、CRLF、无末尾换行、新文件都支持；删除文件本周期不做。
/// </summary>
public static class UnifiedPatchParser
{
    private static readonly Regex HunkHeader = new(
        @"^@@\s+-(?<old>\d+)(?:,(?<oldCount>\d+))?\s+\+(?<new>\d+)(?:,(?<newCount>\d+))?\s+@@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedPatch Parse(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            throw new InvalidOperationException("补丁为空。");
        }

        var lines = patch.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Where(line => !IsWrapper(line))
            .ToArray();
        var files = new List<PatchFile>();
        var index = 0;

        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            if (!lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"第 {index + 1} 行不是补丁头（应为 --- 文件路径）：{Trim(lines[index])}");
            }

            var oldRaw = ParsePath(lines[index][4..], index);
            index++;

            if (index >= lines.Length || !lines[index].StartsWith("+++ ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"第 {index + 1} 行缺少 +++ 文件路径。");
            }

            var newRaw = ParsePath(lines[index][4..], index);
            index++;

            var isNew = oldRaw is null;
            if (newRaw is null)
            {
                throw new InvalidOperationException("P3 不支持删除文件；请保留 +++ 路径。");
            }

            var hunks = new List<PatchHunk>();
            while (index < lines.Length)
            {
                if (lines[index].StartsWith("--- ", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(lines[index]))
                {
                    break;
                }

                if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"第 {index + 1} 行应为 hunk 头（@@ -a,b +c,d @@）：{Trim(lines[index])}");
                }

                var match = HunkHeader.Match(lines[index]);
                if (!match.Success)
                {
                    throw new InvalidOperationException($"第 {index + 1} 行 hunk 头格式无效：{Trim(lines[index])}");
                }

                var oldStart = int.Parse(match.Groups["old"].Value);
                var oldCount = match.Groups["oldCount"].Success
                    ? int.Parse(match.Groups["oldCount"].Value)
                    : 1;
                var newStart = int.Parse(match.Groups["new"].Value);
                var newCount = match.Groups["newCount"].Success
                    ? int.Parse(match.Groups["newCount"].Value)
                    : 1;

                index++;
                var hunkLines = new List<PatchLine>();

                while (index < lines.Length)
                {
                    var line = lines[index];

                    if (line.StartsWith("@@", StringComparison.Ordinal)
                        || line.StartsWith("--- ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        // 末尾空行 / 文件之间的空行是分隔符；只有 hunk 内部的空行才按上下文处理。
                        if (index + 1 >= lines.Length
                            || lines[index + 1].StartsWith("--- ", StringComparison.Ordinal)
                            || lines[index + 1].StartsWith("@@", StringComparison.Ordinal))
                        {
                            break;
                        }

                        hunkLines.Add(new PatchLine(' ', string.Empty));
                        index++;
                        continue;
                    }

                    var kind = line[0];
                    if (kind is not (' ' or '+' or '-' or '\\'))
                    {
                        break;
                    }

                    if (kind == '\\')
                    {
                        if (hunkLines.Count == 0)
                        {
                            throw new InvalidOperationException("“\\ No newline at end of file” 前没有可标记的行。");
                        }

                        hunkLines[^1] = hunkLines[^1] with { NoNewline = true };
                        index++;
                        continue;
                    }

                    hunkLines.Add(new PatchLine(kind, line[1..]));
                    index++;
                }

                if (hunkLines.Count == 0)
                {
                    throw new InvalidOperationException($"第 {index + 1} 行附近的 hunk 没有内容。");
                }

                hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, hunkLines));
            }

            if (hunks.Count == 0)
            {
                throw new InvalidOperationException($"文件 {newRaw} 没有任何 hunk。");
            }

            if (!isNew && !string.Equals(oldRaw, newRaw, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"重命名不受支持：{oldRaw} → {newRaw}");
            }

            if (files.Any(file => string.Equals(file.NewPath, newRaw, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"补丁里重复出现同一文件：{newRaw}");
            }

            files.Add(new PatchFile(oldRaw, newRaw, isNew, hunks));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("补丁里没有任何文件。");
        }

        return new ParsedPatch(files, patch);
    }

    /// <summary>解析路径字段；<c>/dev/null</c> 返回 null。同时做安全校验。</summary>
    private static string? ParsePath(string raw, int lineIndex)
    {
        // 标准 diff 的路径后面可能跟 \t 时间戳。
        var value = raw.Split('\t')[0].Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException($"第 {lineIndex + 1} 行路径为空。");
        }

        if (value == "/dev/null")
        {
            return null;
        }

        value = value.Replace('\\', '/');
        if (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.Length == 0)
        {
            throw new InvalidOperationException($"第 {lineIndex + 1} 行路径为空。");
        }

        if (value.Contains('\0'))
        {
            throw new InvalidOperationException("补丁路径包含非法字符。");
        }

        if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"补丁拒绝 UNC / 设备路径：{value}");
        }

        if (Path.IsPathRooted(value))
        {
            throw new InvalidOperationException($"补丁拒绝绝对路径：{value}");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"补丁拒绝包含 .. 的路径：{value}");
        }

        return string.Join('/', segments);
    }

    private static bool IsWrapper(string line) =>
        line.Trim() is "*** Begin Patch" or "*** End Patch";

    private static string Trim(string line) =>
        line.Length <= 120 ? line : string.Concat(line.AsSpan(0, 120), "…");
}
