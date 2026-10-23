using System.IO;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 目录形式的内容源（用户插件 / 测试夹具）。
/// 仍然做「相对路径 + 全路径前缀」双重越界校验：junction / <c>..</c> / 绝对路径都不能逃出插件目录。
/// </summary>
public sealed class DirectoryPluginContent : IPluginContentSource
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootPrefix;

    public DirectoryPluginContent(string root)
    {
        DisplayPath = Path.GetFullPath(root);
        _rootPrefix = DisplayPath.EndsWith(Path.DirectorySeparatorChar)
            ? DisplayPath
            : DisplayPath + Path.DirectorySeparatorChar;
    }

    public string DisplayPath { get; }

    public bool TryRead(string relativePath, out string content, out long byteLength, out string? error)
    {
        content = string.Empty;
        byteLength = 0;

        var candidate = Resolve(relativePath, out error);
        if (candidate is null)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(candidate);
            if (!info.Exists)
            {
                error = $"文件不存在：{relativePath.Replace('\\', '/')}";
                return false;
            }

            if (info.Length > PluginContentLimits.MaxFileBytes)
            {
                error = "文件过大";
                return false;
            }

            content = File.ReadAllText(candidate);
            byteLength = info.Length;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取失败：{ex.Message}";
            return false;
        }
    }

    private string? Resolve(string relativePath, out string? error)
    {
        error = null;

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            error = "不允许绝对路径";
            return null;
        }

        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            error = "不允许 ..";
            return null;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(DisplayPath, normalized));
            if (!candidate.StartsWith(_rootPrefix, PathComparison))
            {
                error = "必须位于插件目录内";
                return null;
            }

            return candidate;
        }
        catch (ArgumentException)
        {
            error = "路径非法";
            return null;
        }
    }
}
