using System.Text;
using System.Text.RegularExpressions;
using SDemo.DesktopAgent.Editing;

namespace SDemo.DesktopAgent.Services;

/// <summary>搜索结果：给模型的文本 + 实际命中的工作区相对路径（用于读前写登记）。</summary>
public sealed record WorkspaceSearchResult(string Text, IReadOnlyList<string> MatchedPaths);

/// <summary>
/// 纯 .NET 的工作区检索：目录树、glob、正则搜索。不依赖外部 rg；
/// 统一排除构建目录与版本库，跳过二进制 / 超大文件，所有路径都先过 <see cref="WorkspacePath"/>。
/// </summary>
public sealed class WorkspaceSearch
{
    public const int MaxSearchFileBytes = 512 * 1024;
    public const int MaxMatchesPerFile = 20;
    public const int MaxResultLines = 80;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", ".tmp", "TestResults"
    };

    /// <summary>
    /// 搜索：默认按正则；没有命中时自动做一次字面量回退，再把 <c>Type.Member</c> 退化成成员名。
    /// 这样模型不用知道正则元字符，也不会因为写了 <c>ParserSettings.MaximumDisplayWidth</c>
    /// 而搜不到只写 <c>MaximumDisplayWidth</c> 的属性行。回退会在结果首行说明用了哪种模式。
    /// </summary>
    public WorkspaceSearchResult Search(
        string root,
        string pattern,
        string glob = "**/*",
        int maxResults = MaxResultLines,
        int contextLines = 0,
        bool caseSensitive = false,
        bool literal = false)
    {
        var primary = SearchCore(root, pattern, glob, maxResults, contextLines, caseSensitive, literal);
        if (literal || HasHits(primary) || primary.Text.StartsWith("搜索超时", StringComparison.Ordinal))
        {
            return primary;
        }

        if (primary.Text.StartsWith("正则无效", StringComparison.Ordinal))
        {
            var escaped = SearchCore(root, pattern, glob, maxResults, contextLines, caseSensitive, literal: true);
            return HasHits(escaped)
                ? WithNote(escaped, $"正则无效，已按字面量搜索「{pattern}」：")
                : primary;
        }

        var literalAttempt = SearchCore(root, pattern, glob, maxResults, contextLines, caseSensitive, literal: true);
        if (HasHits(literalAttempt))
        {
            return WithNote(literalAttempt, $"没有正则命中，已按字面量回退「{pattern}」：");
        }

        var member = LastDottedMember(pattern);
        if (member is not null)
        {
            var memberAttempt = SearchCore(root, member, glob, maxResults, contextLines, caseSensitive, literal: true);
            if (HasHits(memberAttempt))
            {
                return WithNote(memberAttempt, $"没有找到「{pattern}」，已按成员名「{member}」回退：");
            }
        }

        return primary;
    }

    private static bool HasHits(WorkspaceSearchResult result) =>
        !result.Text.StartsWith("未找到", StringComparison.Ordinal)
        && !result.Text.StartsWith("正则无效", StringComparison.Ordinal)
        && !result.Text.StartsWith("搜索超时", StringComparison.Ordinal);

    private static WorkspaceSearchResult WithNote(WorkspaceSearchResult result, string note) =>
        new($"{note}\n{result.Text}", result.MatchedPaths);

    /// <summary>把 <c>Type.Member</c> 退化成 <c>Member</c>；只处理明显是符号名的输入。</summary>
    private static string? LastDottedMember(string pattern)
    {
        var parts = pattern.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[^1].Length < 4)
        {
            return null;
        }

        var type = parts[^2];
        return type.Length > 0 && char.IsUpper(type[0]) ? parts[^1] : null;
    }

    /// <summary>正则搜索的实现；<paramref name="literal"/> 为 true 时按字面量转义。</summary>
    private WorkspaceSearchResult SearchCore(
        string root,
        string pattern,
        string glob,
        int maxResults,
        int contextLines,
        bool caseSensitive,
        bool literal)
    {
        var limit = Math.Clamp(maxResults, 1, 200);
        var context = Math.Clamp(contextLines, 0, 5);

        var expression = literal ? Regex.Escape(pattern) : pattern;

        Regex regex;
        try
        {
            regex = new Regex(
                expression,
                RegexOptions.CultureInvariant
                | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return new WorkspaceSearchResult($"正则无效：{ex.Message}", []);
        }

        var matchedPaths = new List<string>();
        var lines = new List<string>();
        var files = 0;

        foreach (var relative in EnumerateFiles(root, glob))
        {
            if (lines.Count >= limit)
            {
                break;
            }

            var full = WorkspacePath.Resolve(root, relative, allowRoot: false);
            if (!IsSearchable(full))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string[] source;
            try
            {
                source = SplitLines(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var hits = new List<int>();
            try
            {
                for (var index = 0; index < source.Length; index++)
                {
                    if (regex.IsMatch(source[index]))
                    {
                        hits.Add(index);
                        if (hits.Count >= MaxMatchesPerFile)
                        {
                            break;
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return new WorkspaceSearchResult("搜索超时（单文件正则限制 1 秒），请缩小 glob 或简化正则。", []);
            }

            if (hits.Count == 0)
            {
                continue;
            }

            files++;
            matchedPaths.Add(relative);

            var shown = new HashSet<int>();
            foreach (var hit in hits)
            {
                var from = Math.Max(0, hit - context);
                var to = Math.Min(source.Length - 1, hit + context);

                for (var index = from; index <= to; index++)
                {
                    if (!shown.Add(index))
                    {
                        continue;
                    }

                    if (lines.Count >= limit)
                    {
                        break;
                    }

                    var content = source[index].Length <= 300
                        ? source[index]
                        : string.Concat(source[index].AsSpan(0, 300), "…");

                    lines.Add($"{relative}:{index + 1}: {content}");
                }
            }

            if (lines.Count >= limit)
            {
                break;
            }
        }

        if (lines.Count == 0)
        {
            return new WorkspaceSearchResult("未找到匹配；建议放宽 glob 或换关键词。", []);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"找到 {lines.Count} 条匹配（{files} 个文件，最多显示 {limit} 条）：");
        builder.AppendJoin('\n', lines);

        if (lines.Count >= limit)
        {
            builder.AppendLine().Append("…（结果已达上限，可缩小 glob 后重试）");
        }

        return new WorkspaceSearchResult(builder.ToString(), matchedPaths);
    }

    /// <summary>只返回匹配 glob 的路径，按路径排序。</summary>
    public IReadOnlyList<string> Glob(string root, string pattern, int maxResults = 200)
    {
        var limit = Math.Clamp(maxResults, 1, 1000);
        return EnumerateFiles(root, pattern).Take(limit).ToArray();
    }

    public string ListTree(string root, string path = ".", int depth = 2, int maxEntries = 200)
    {
        var directory = WorkspacePath.Resolve(root, path, allowRoot: true);
        var limit = Math.Clamp(maxEntries, 1, 2000);
        var levels = Math.Clamp(depth, 1, 8);

        if (File.Exists(directory))
        {
            return $"文件：{WorkspacePath.Relative(root, directory)}";
        }

        if (!Directory.Exists(directory))
        {
            return $"目录不存在：{path}";
        }

        var lines = new List<string>();
        Walk(directory, 0, levels, limit, root, lines);

        if (lines.Count == 0)
        {
            return "（空目录）";
        }

        if (lines.Count >= limit)
        {
            lines.Add($"…（已显示前 {limit} 项）");
        }

        return string.Join('\n', lines);
    }

    /// <summary>按 glob 枚举文件；自动跳过排除目录、重解析点与工作区外的路径。</summary>
    public IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedGlob = NormalizeGlob(pattern);
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(normalizedRoot);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory);
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsReparsePoint(file))
                {
                    continue;
                }

                var relative = WorkspacePath.Relative(normalizedRoot, file);
                if (MatchesGlob(relative, normalizedGlob))
                {
                    results.Add(relative);
                }
            }

            foreach (var child in childDirectories)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name) || IsReparsePoint(child))
                {
                    continue;
                }

                stack.Push(child);
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>glob → 正则：<c>**</c>、<c>*</c>、<c>?</c>；无斜杠的模式视为 <c>**/pattern</c>。</summary>
    public static bool MatchesGlob(string relativePath, string pattern)
    {
        var normalized = NormalizeGlob(pattern);
        var regex = GlobToRegex(normalized);
        return Regex.IsMatch(
            relativePath.Replace('\\', '/'),
            regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static string NormalizeGlob(string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "**/*" : pattern.Trim().Replace('\\', '/');
        value = value.TrimStart('/');
        if (!value.Contains('/'))
        {
            value = "**/" + value;
        }

        return value;
    }

    private static string GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        index++;
                        if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                        {
                            index++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;

                case '?':
                    builder.Append("[^/]");
                    break;

                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static bool IsSearchable(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists
                && info.Length <= MaxSearchFileBytes
                && !WorkspacePath.LooksBinary(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void Walk(
        string directory,
        int level,
        int maxDepth,
        int maxEntries,
        string root,
        List<string> lines)
    {
        if (level >= maxDepth || lines.Count >= maxEntries)
        {
            return;
        }

        List<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory)
                .Where(child => !ExcludedDirectories.Contains(Path.GetFileName(child))
                    && !IsReparsePoint(child))
                .OrderBy(child => Directory.Exists(child) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (lines.Count >= maxEntries)
            {
                return;
            }

            var relative = WorkspacePath.Relative(root, child);
            var indent = new string(' ', level * 2);

            if (Directory.Exists(child))
            {
                lines.Add($"{indent}{relative}/");
                Walk(child, level + 1, maxDepth, maxEntries, root, lines);
            }
            else
            {
                lines.Add($"{indent}{relative}");
            }
        }
    }
}
