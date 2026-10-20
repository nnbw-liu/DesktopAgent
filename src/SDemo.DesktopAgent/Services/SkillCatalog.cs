using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>技能库里的一个技能（文件名即技能名）。</summary>
public sealed record SkillInfo(string Name, string Description);

/// <summary>
/// 本地技能库：读取 %APPDATA%\SDemo\DesktopAgent\skills\*.md。
/// 文件可以带 YAML frontmatter（name / description / version），也可以直接写正文；
/// 列表只展示技能名 + 一句话说明，选中后由聊天服务把全文注入系统提示词。
/// </summary>
public sealed class SkillCatalog
{
    private readonly string _root;

    public SkillCatalog()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent",
            "skills"))
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
            .Select(file => new SkillInfo(
                Path.GetFileNameWithoutExtension(file),
                ReadSummary(file)))
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
    /// 技能摘要：优先取 frontmatter 的 description；没有 frontmatter 时
    /// 退回“第一行非空且不是标题”的文本。
    /// </summary>
    private static string ReadSummary(string file)
    {
        var lines = File.ReadLines(file)
            .Take(40)
            .Select(line => line.Trim())
            .ToList();

        if (lines.Count > 0 && lines[0] == "---")
        {
            var end = lines.FindIndex(1, line => line == "---");
            if (end > 0)
            {
                for (var i = 1; i < end; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    {
                        return line["description:".Length..].Trim().Trim('"', '\'', '“', '”');
                    }
                }

                return lines.Skip(end + 1)
                    .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
            }
        }

        return lines.FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
    }
}
