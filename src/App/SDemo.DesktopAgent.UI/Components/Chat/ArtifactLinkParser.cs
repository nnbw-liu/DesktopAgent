using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Components.Chat;

/// <summary>
/// 从助手正文里找 Archify 产物路径（只认工作区内的 <c>.archify/.../*.html</c>）。
/// 这里只做文本解析；真正打开前还会按工作区根做一次路径收窄，不接受模型给的任意绝对路径。
/// </summary>
public static partial class ArtifactLinkParser
{
    [GeneratedRegex(
        @"(?<![A-Za-z0-9._:\\/-])(?:\.archify[\\/])(?:[A-Za-z0-9._-]+[\\/])+[A-Za-z0-9._-]+\.html?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactPattern();

    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ArtifactPattern().Matches(text))
        {
            var path = match.Value.Replace('\\', '/').Trim();

            if (path.Split('/').Any(segment => segment is ".." or ""))
            {
                continue;
            }

            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }
}
