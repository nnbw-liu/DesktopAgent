using System.Globalization;
using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>技能库里的一个技能（文件名即技能名）。</summary>
/// <param name="Name">技能名（就是文件名）。</param>
/// <param name="Description">一句话说明（悬浮提示用）。</param>
/// <param name="Icon">frontmatter 里的 <c>icon</c>；没写时为空，由 <see cref="Glyph"/> 取名字首字兜底。</param>
public sealed record SkillInfo(string Name, string Description, string Icon = "")
{
    /// <summary>输入框 chip 里显示的字形：优先用 authored 的 <c>icon</c>，缺省取名字首字。</summary>
    public string Glyph => GlyphOf(!string.IsNullOrWhiteSpace(Icon) ? Icon : Name);

    /// <summary>取一段文本的第一个字素：英文转大写，emoji 不会被拆成半个代理对。</summary>
    public static string GlyphOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "?";
        }

        var trimmed = text.Trim();
        var glyph = StringInfo.GetNextTextElement(trimmed);
        return glyph.ToUpperInvariant();
    }
}

/// <summary>
/// 本地技能库：读取 %APPDATA%\SDemo\DesktopAgent\skills\*.md。
/// 文件可以带 YAML frontmatter（name / description / icon / version），也可以直接写正文；
/// 列表只展示技能名 + 一句话说明 + 字形图标，选中后由聊天服务把全文注入系统提示词。
/// </summary>
public sealed class SkillCatalog
{
    private readonly string _root;

    public SkillCatalog(IAppPaths paths)
        : this(paths.SkillsRoot)
    {
    }

    public SkillCatalog(string root)
    {
        _root = root;
    }

    public string Root => _root;

    public IReadOnlyList<SkillInfo> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return Directory.EnumerateFiles(_root, "*.md")
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var (description, icon) = ReadMetadata(file);
                return new SkillInfo(name, description, icon);
            })
            .ToList();
    }

    /// <summary>读取技能全文；名字只取文件名，防止 ../ 越界。</summary>
    public string? ReadContent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var safeName = Path.GetFileNameWithoutExtension(name.Trim());
        var file = Path.Combine(_root, safeName + ".md");

        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>
    /// 技能摘要 + 图标：优先取 frontmatter 的 description / icon；没有 frontmatter 时
    /// 摘要退回“第一行非空且不是标题”的文本，图标由调用方用名字兜底。
    /// </summary>
    private static (string Description, string Icon) ReadMetadata(string file)
    {
        var lines = File.ReadLines(file)
            .Take(40)
            .Select(line => line.Trim())
            .ToList();

        var icon = string.Empty;

        if (lines.Count > 0 && lines[0] == "---")
        {
            var end = lines.FindIndex(1, line => line == "---");
            if (end > 0)
            {
                var description = string.Empty;

                for (var i = 1; i < end; i++)
                {
                    var line = lines[i];

                    if (description.Length == 0 && line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    {
                        description = Unquote(line["description:".Length..]);
                    }
                    else if (icon.Length == 0 && line.StartsWith("icon:", StringComparison.OrdinalIgnoreCase))
                    {
                        icon = Unquote(line["icon:".Length..]);
                    }
                }

                if (description.Length == 0)
                {
                    description = lines.Skip(end + 1)
                        .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
                }

                return (description, icon);
            }
        }

        var summary = lines.FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
        return (summary, icon);
    }

    private static string Unquote(string value) => value.Trim().Trim('"', '\'', '“', '”');
}
