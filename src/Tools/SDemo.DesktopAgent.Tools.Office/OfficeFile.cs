using System.IO;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// Office 工具的路径 jail：读取允许「工作区 + 会话附件目录」，写入只允许工作区。
/// 逐段拒绝重解析点（符号链接 / junction），拒绝 UNC / 设备路径与 <c>..</c>。
/// </summary>
internal static class OfficeFile
{
    public const long MaxReadBytes = 50L * 1024 * 1024;
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    private static readonly HashSet<string> ReadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx"
    };

    private static readonly HashSet<string> WriteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx"
    };

    /// <summary>解析一个已存在的 Office 文件；失败返回错误文案。</summary>
    public static (string? FullPath, string? DisplayName, string? Error) ResolveRead(
        ToolContext context,
        string? path)
    {
        var resolved = Resolve(context, path, allowMissing: false);
        if (resolved.Error is not null)
        {
            return (null, null, resolved.Error);
        }

        var full = resolved.FullPath!;
        var extension = Path.GetExtension(full);

        if (!ReadExtensions.Contains(extension))
        {
            return (null, null, Unsupported(extension));
        }

        var size = new FileInfo(full).Length;
        if (size <= 0)
        {
            return (null, null, $"Office 文件为空：{path}");
        }

        // 工作区里的文档允许 50MB；上传附件沿用 25MB 上限。
        var limit = IsWorkspaceRoot(context, resolved.Root!) ? MaxReadBytes : MaxAttachmentBytes;
        if (size > limit)
        {
            return (null, null,
                $"Office 文件过大（{size / 1024 / 1024} MB），上限 {limit / 1024 / 1024} MB：{path}");
        }

        return (full, Path.GetFileName(full), null);
    }

    /// <summary>解析一个写入目标：只允许工作区内，允许尚不存在（新建）。</summary>
    public static (string? FullPath, string? Error) ResolveWrite(ToolContext context, string? path)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return (null, "当前会话没有工作区，无法写入 Office 文件。");
        }

        var resolved = Resolve(context, path, allowMissing: true, onlyWorkspace: true);
        if (resolved.Error is not null)
        {
            return (null, resolved.Error);
        }

        var extension = Path.GetExtension(resolved.FullPath!);
        if (!WriteExtensions.Contains(extension))
        {
            return (null, Unsupported(extension));
        }

        return (resolved.FullPath, null);
    }

    public static string FormatOf(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static string DisplayFormat(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" => "Word",
        ".xlsx" => "Excel",
        ".pptx" => "PowerPoint",
        _ => extension
    };

    private static string Unsupported(string extension) => string.IsNullOrEmpty(extension)
        ? "只支持 .docx / .xlsx / .pptx；旧版 .doc / .xls / .ppt 请先另存为新格式。"
        : $"不支持的文件类型 {extension}：只支持 .docx / .xlsx / .pptx；旧版格式请先另存。";

    private static bool IsWorkspaceRoot(ToolContext context, string root) =>
        !string.IsNullOrWhiteSpace(context.WorkspaceRoot)
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.WorkspaceRoot)),
            StringComparison.OrdinalIgnoreCase);

    private sealed record Resolved(string? FullPath, string? Root, string? Error);

    private static Resolved Resolve(
        ToolContext context,
        string? path,
        bool allowMissing,
        bool onlyWorkspace = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Resolved(null, null, "Office 文件路径为空。");
        }

        var value = path.Trim();
        if (value.Contains('\0'))
        {
            return new Resolved(null, null, "Office 文件路径包含非法字符。");
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal))
        {
            return new Resolved(null, null, $"拒绝 UNC / 设备路径：{path}");
        }

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            roots.Add(context.WorkspaceRoot);
        }

        if (!onlyWorkspace)
        {
            roots.AddRange(context.ReadOnlyRoots.Where(root => !string.IsNullOrWhiteSpace(root)));
        }

        if (roots.Count == 0)
        {
            return new Resolved(null, null, "没有允许访问的根目录。");
        }

        if (Path.IsPathRooted(value))
        {
            string full;
            try
            {
                full = Path.GetFullPath(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new Resolved(null, null, $"Office 文件路径无效：{path}");
            }

            var root = roots.FirstOrDefault(candidate => SandboxPathGuard.IsUnder(candidate, full));
            return root is null
                ? new Resolved(null, null, $"Office 文件路径不在允许的工作区或会话附件目录内：{path}")
                : Validate(full, root, path, allowMissing);
        }

        var segments = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".."))
        {
            return new Resolved(null, null, $"Office 文件路径不能包含 ..：{path}");
        }

        foreach (var root in roots)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(
                Path.Combine(normalizedRoot, string.Join(Path.DirectorySeparatorChar, segments)));

            if (!SandboxPathGuard.IsUnder(normalizedRoot, full))
            {
                return new Resolved(null, null, $"Office 文件路径超出允许根目录：{path}");
            }

            if (File.Exists(full))
            {
                return Validate(full, normalizedRoot, path, allowMissing);
            }

            if (allowMissing && onlyWorkspace)
            {
                // 目标可能还不存在；但父目录里的符号链接 / junction 仍然要检查。
                return Validate(full, normalizedRoot, path, allowMissing: true);
            }
        }

        return new Resolved(null, null, $"Office 文件不存在：{path}");
    }

    private static Resolved Validate(string fullPath, string root, string original, bool allowMissing)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var relative = Path.GetRelativePath(normalizedRoot, fullPath);

            var current = normalizedRoot;
            foreach (var segment in relative.Split(
                         Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    return new Resolved(null, null, $"Office 文件路径不能包含 . / ..：{original}");
                }

                current = Path.Combine(current, segment);

                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    break;
                }

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return new Resolved(null, null, $"Office 路径包含符号链接或 junction：{original}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Resolved(null, null, $"Office 文件路径无效：{original}");
        }

        if (!File.Exists(fullPath) && !allowMissing)
        {
            return new Resolved(null, null, $"Office 文件不存在：{original}");
        }

        return new Resolved(fullPath, root, null);
    }
}
