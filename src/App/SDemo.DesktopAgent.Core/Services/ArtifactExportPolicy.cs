using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// P4：预览产物的导出文件名策略。只接受 Archify 能产出的图片 / SVG / WebM；
/// 去掉目录部分，拒绝非法扩展名，命名冲突时追加时间戳。
/// </summary>
public static class ArtifactExportPolicy
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".svg", ".webm"
        };

    public static bool TryCreateTarget(
        string exportsRoot,
        string? suggestedName,
        DateTimeOffset now,
        out string? target)
    {
        target = null;

        if (string.IsNullOrWhiteSpace(exportsRoot) || string.IsNullOrWhiteSpace(suggestedName))
        {
            return false;
        }

        var fileName = Path.GetFileName(suggestedName.Trim());

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);

        if (!AllowedExtensions.Contains(extension))
        {
            return false;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        var root = Path.GetFullPath(exportsRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));

        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (File.Exists(candidate))
        {
            candidate = Path.Combine(
                root,
                $"{Path.GetFileNameWithoutExtension(fileName)}-{now:yyyyMMdd-HHmmssfff}{extension}");
        }

        target = candidate;
        return true;
    }

    public static bool IsAllowedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && AllowedExtensions.Contains(extension);
}
