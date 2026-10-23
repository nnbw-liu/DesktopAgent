using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Editing;

/// <summary>
/// 工作区路径校验：只接受相对路径，拒绝绝对路径 / UNC / 设备路径 / <c>..</c> /
/// 经过重解析点（符号链接、junction）的路径。文件和编辑工具共用这一份规则。
/// </summary>
public static class WorkspacePath
{
    public static string Resolve(string root, string? relative, bool allowRoot)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var value = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();

        if (value.Contains('\0'))
        {
            throw new InvalidOperationException("路径包含非法字符。");
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"拒绝 UNC / 设备路径：{relative}");
        }

        if (Path.IsPathRooted(value))
        {
            throw new InvalidOperationException($"拒绝绝对路径：{relative}");
        }

        var segments = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"拒绝包含 .. 的路径：{relative}");
        }

        if (!allowRoot && segments.Length == 0)
        {
            throw new InvalidOperationException("必须指定具体文件路径，不能是工作区根目录。");
        }

        var full = Path.GetFullPath(
            Path.Combine(normalizedRoot, string.Join(Path.DirectorySeparatorChar, segments)));

        if (!SandboxPathGuard.IsUnder(normalizedRoot, full))
        {
            throw new InvalidOperationException($"路径超出工作区范围：{relative}");
        }

        EnsureNoReparsePoint(normalizedRoot, segments);
        return full;
    }

    /// <summary>把绝对路径转成统一使用 <c>/</c> 的工作区相对路径。</summary>
    public static string Relative(string root, string full) =>
        Path.GetRelativePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), full)
            .Replace('\\', '/');

    /// <summary>规范化显示用相对路径（不做安全校验；调用方应先用 <see cref="Resolve"/>）。</summary>
    public static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    /// <summary>前 8KB 含 NUL 视为二进制，不做文本搜索 / 按行读取。</summary>
    public static bool LooksBinary(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            Span<byte> buffer = stackalloc byte[8192];
            var read = stream.Read(buffer);
            return buffer[..read].Contains((byte)0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void EnsureNoReparsePoint(string normalizedRoot, IReadOnlyList<string> segments)
    {
        var current = normalizedRoot;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if (!File.Exists(current) && !Directory.Exists(current))
            {
                // 后面不存在的目录 / 文件由调用方处理；已存在的部分都已经检查过。
                return;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("路径包含符号链接或 junction，已拒绝。");
            }
        }
    }
}
