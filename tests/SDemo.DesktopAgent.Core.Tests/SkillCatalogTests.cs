using System.IO;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>技能库：frontmatter 的 description / icon 解析，以及图标的兜底规则。</summary>
public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sdemo-skill-tests",
        Guid.NewGuid().ToString("N"));

    public SkillCatalogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void 带_frontmatter_的技能_读出说明与图标()
    {
        Write("总结.md", """
            ---
            name: 总结
            description: 一句话结论，再列要点。
            icon: "总"
            version: 1.0
            ---

            # 总结
            """);

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("总结", skill.Name);
        Assert.Equal("一句话结论，再列要点。", skill.Description);
        Assert.Equal("总", skill.Icon);
        Assert.Equal("总", skill.Glyph);
    }

    [Fact]
    public void 没写_icon_时_兜底回名字首字()
    {
        Write("代码解释.md", """
            ---
            description: 逐段说明代码。
            ---

            正文
            """);

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal(string.Empty, skill.Icon);
        Assert.Equal("代", skill.Glyph);
    }

    [Fact]
    public void 没有_frontmatter_时_摘要取首行正文_图标取名字首字()
    {
        Write("文风.md", """
            # 文风

            保持简洁，删掉形容词。
            """);

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("保持简洁，删掉形容词。", skill.Description);
        Assert.Equal("文", skill.Glyph);
    }

    [Fact]
    public void 英文名首字转大写_图标可写多个字符只取第一个()
    {
        Write("review.md", """
            ---
            description: Review code.
            icon: globe
            ---
            """);

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("G", skill.Glyph);
        Assert.Equal("R", SkillInfo.GlyphOf("review"));
    }

    [Fact]
    public void emoji_图标不会被拆成半个代理对()
    {
        Write("翻译.md", """
            ---
            description: 翻译成目标语言。
            icon: 🌐
            ---
            """);

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("🌐", skill.Glyph);
        Assert.Equal(2, skill.Glyph.Length);
    }

    [Fact]
    public void 技能目录不存在时返回空列表()
    {
        var catalog = new SkillCatalog(Path.Combine(_root, "missing"));

        Assert.Empty(catalog.List());
    }

    [Fact]
    public void 列表按文件名排序()
    {
        Write("b.md", "B");
        Write("a.md", "A");
        Write("c.md", "C");

        var names = new SkillCatalog(_root).List().Select(skill => skill.Name).ToArray();

        Assert.Equal(new[] { "a", "b", "c" }, names);
    }

    private void Write(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_root, fileName), content);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响测试结果。
        }
    }
}
