using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 把插件 skill 渲染成 system 提示词，并把用户输入包进固定定界符的 user 消息。
/// 约定：skill 只进 system，用户文本只进 user，二者永不拼接（见设计文档 §6）。
/// </summary>
public sealed class PluginPromptComposer
{
    public const string InputOpenTag = "<待处理文本>";
    public const string InputCloseTag = "</待处理文本>";

    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*([a-z][a-zA-Z0-9_]*)\s*(\.\s*label\s*)?\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>渲染 system 提示词：去掉 frontmatter，替换 <c>{{key}}</c> 与 <c>{{key.label}}</c>。</summary>
    public string BuildSystemPrompt(
        PluginCapability capability,
        IReadOnlyDictionary<string, string> values)
    {
        var body = StripFrontmatter(capability.SkillContent ?? string.Empty);

        var rendered = PlaceholderPattern.Replace(body, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
            {
                // 校验器已经挡掉了未定义占位符；真走到这里就原样保留，避免静默变成空字符串。
                return match.Value;
            }

            if (!match.Groups[2].Success)
            {
                return value;
            }

            var parameter = capability.FindParameter(key);
            return parameter?.ResolveLabel(value) ?? value;
        });

        // 双语对照的格式契约由宿主追加（M6 实现），插件作者不需要也不应该自己写。

        return rendered.Trim();
    }

    /// <summary>
    /// 把用户输入包进定界符；输入里的 <c>&lt;</c> 做全角转义，
    /// 防止用户伪造出 <c>&lt;/待处理文本&gt;</c> 之类的分段。
    /// </summary>
    public string WrapUserInput(string? text)
    {
        var escaped = (text ?? string.Empty)
            .Replace("<", "＜", StringComparison.Ordinal);

        return $"{InputOpenTag}\n{escaped}\n{InputCloseTag}";
    }

    /// <summary>去掉 YAML frontmatter，只留正文。</summary>
    public static string StripFrontmatter(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var first = Array.FindIndex(lines, line => line.Trim().Length > 0);
        if (first < 0 || lines[first].Trim() != "---")
        {
            return normalized;
        }

        var end = Array.FindIndex(lines, first + 1, line => line.Trim() == "---");
        if (end < 0)
        {
            // 只有开头没有结束：当作没有 frontmatter。
            return normalized;
        }

        return string.Join('\n', lines[(end + 1)..]).TrimStart('\n');
    }
}
