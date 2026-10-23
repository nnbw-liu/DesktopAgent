using System.Globalization;
using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>技能库里的一个技能（文件名即技能名）。</summary>
/// <param name="Name">技能名（就是文件名）。</param>
/// <param name="Description">一句话说明（悬浮提示与索引共用）。</param>
/// <param name="Icon">frontmatter 里的 <c>icon</c>；没写时为空，由 <see cref="Glyph"/> 取名字首字兜底。</param>
/// <param name="WhenToUse">索引里的「何时用」；没有时退回 description。</param>
/// <param name="Tags">路由用的标签（frontmatter 的 <c>tags</c>，逗号分隔）。</param>
public sealed record SkillInfo(
    string Name,
    string Description,
    string Icon = "",
    string WhenToUse = "",
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>输入框 chip 里显示的字形：优先用 authored 的 <c>icon</c>，缺省取名字首字。</summary>
    public string Glyph => GlyphOf(!string.IsNullOrWhiteSpace(Icon) ? Icon : Name);

    /// <summary>非空标签列表（没有 frontmatter 时为空）。</summary>
    public IReadOnlyList<string> TagList => Tags ?? [];

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
/// 文件可以带 YAML frontmatter（name / description / icon / when-to-use / tags / version），
/// 也可以直接写正文；列表只展示技能名 + 一句话说明 + 字形图标。
/// M2 起系统提示只注入索引（<see cref="Index"/>），正文由 <c>load_skill</c> 按需读取；
/// 用户手选技能仍由聊天服务注入全文。
/// </summary>
public sealed class SkillCatalog : ISkillLibrary
{
    private const string WhenToUseKey = "when-to-use:";

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
                var metadata = ReadMetadata(file);
                return new SkillInfo(
                    name,
                    metadata.Description,
                    metadata.Icon,
                    metadata.WhenToUse,
                    metadata.Tags);
            })
            .ToList();
    }

    /// <summary>M2：只读 frontmatter 的索引元数据，不读正文。</summary>
    public IReadOnlyList<SkillIndexEntry> Index() =>
        List()
            .Select(skill => new SkillIndexEntry(
                skill.Name,
                skill.Description,
                string.IsNullOrWhiteSpace(skill.WhenToUse) ? skill.Description : skill.WhenToUse,
                skill.TagList))
            .ToArray();

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

    private sealed record Metadata(
        string Description,
        string Icon,
        string WhenToUse,
        IReadOnlyList<string> Tags);

    /// <summary>
    /// 技能摘要 + 图标 + 路由元数据：优先取 frontmatter；没有 frontmatter 时
    /// 摘要退回“第一行非空且不是标题”的文本，图标由调用方用名字兜底。
    /// </summary>
    private static Metadata ReadMetadata(string file)
    {
        var lines = File.ReadLines(file)
            .Take(60)
            .Select(line => line.Trim())
            .ToList();

        if (lines.Count > 0 && lines[0] == "---")
        {
            var end = lines.FindIndex(1, line => line == "---");
            if (end > 0)
            {
                var description = string.Empty;
                var icon = string.Empty;
                var whenToUse = string.Empty;
                IReadOnlyList<string> tags = [];

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
                    else if (whenToUse.Length == 0
                             && (line.StartsWith(WhenToUseKey, StringComparison.OrdinalIgnoreCase)
                                 || line.StartsWith("when_to_use:", StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = line.StartsWith(WhenToUseKey, StringComparison.OrdinalIgnoreCase)
                            ? line[WhenToUseKey.Length..]
                            : line["when_to_use:".Length..];
                        whenToUse = Unquote(value);
                    }
                    else if (tags.Count == 0 && line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        tags = ParseTags(line["tags:".Length..]);
                    }
                }

                if (description.Length == 0)
                {
                    description = lines.Skip(end + 1)
                        .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
                }

                return new Metadata(description, icon, whenToUse, tags);
            }
        }

        var summary = lines.FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
        return new Metadata(summary, string.Empty, string.Empty, []);
    }

    /// <summary>支持 <c>[a, b]</c> 与 <c>a, b</c> 两种写法；空值返回空列表。</summary>
    internal static IReadOnlyList<string> ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Unquote(string value) => value.Trim().Trim('"', '\'', '“', '”');
}
