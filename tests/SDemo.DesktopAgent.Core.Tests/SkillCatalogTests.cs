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
    public void 读到_disable_model_invocation_标记()
    {
        Write("重写.md", """
            ---
            description: 整文件重写，风险较高。
            disable-model-invocation: true
            ---
            正文
            """);

        Write("总结.md", """
            ---
            description: 压成三句话。
            ---
            正文
            """);

        var catalog = new SkillCatalog(_root);
        var skills = catalog.List();

        Assert.True(skills.Single(skill => skill.Name == "重写").DisableModelInvocation);
        Assert.False(skills.Single(skill => skill.Name == "总结").DisableModelInvocation);

        // 索引里带着同一个标记，供 SkillIndexBuilder 过滤。
        Assert.True(catalog.Index().Single(entry => entry.Name == "重写").DisableModelInvocation);
    }

    [Fact]
    public void 显式写_false_不启用标记()
    {
        Write("普通.md", """
            ---
            description: 普通技能。
            disable-model-invocation: false
            ---
            """);

        Assert.False(Assert.Single(new SkillCatalog(_root).List()).DisableModelInvocation);
    }

    [Fact]
    public void 没写_icon_时_兜底回名字首字()    {
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

    [Fact]
    public void 解析_when_to_use_与_tags()
    {
        Write("复盘.md", """
            ---
            description: 复盘一段改动的得失。
            when-to-use: 任务完成或失败后需要总结经验时
            tags: [复盘, 总结, review]
            ---

            正文
            """);

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("任务完成或失败后需要总结经验时", skill.WhenToUse);
        Assert.Equal(["复盘", "总结", "review"], skill.TagList);
    }

    [Fact]
    public void 索引只含元数据不带正文()
    {
        Write("总结.md", """
            ---
            description: 一句话结论。
            when-to-use: 长文需要压缩时
            tags: 总结
            ---

            这是很长的正文，不应该出现在索引里。
            """);

        var entry = Assert.Single(new SkillCatalog(_root).Index());

        Assert.Equal("总结", entry.Name);
        Assert.Equal("一句话结论。", entry.Description);
        Assert.Equal("长文需要压缩时", entry.WhenToUse);
        Assert.Equal(["总结"], entry.Tags);
    }

    [Fact]
    public void 没有_when_to_use_时索引退回_description()
    {
        Write("翻译.md", """
            ---
            description: 翻译成目标语言。
            ---
            """);

        var entry = Assert.Single(new SkillCatalog(_root).Index());

        Assert.Equal(entry.Description, entry.WhenToUse);
    }

    [Fact]
    public void 包名带点时不会被当成扩展名截断()
    {
        WritePackage("my.skill", "---\ndescription: 带点的包\n---\n", ("references/x.md", "ok"));

        var catalog = new SkillCatalog(_root);

        Assert.Equal("my.skill", Assert.Single(catalog.List()).Name);
        Assert.True(catalog.ReadFile("my.skill", "references/x.md").IsOk);
    }

    [Fact]
    public void 目录包被索引且正文读_SKILL_md()
    {
        WritePackage("archify", """
            ---
            description: 画架构图。
            metadata:
              version: "3.2"
            ---

            包正文
            """,
            ("references/delivery.md", "交付契约"));

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("archify", skill.Name);
        Assert.Equal(SkillKind.Package, skill.Kind);
        Assert.Equal("3.2", skill.Version);
        Assert.Equal(Path.Combine(_root, "archify"), skill.RootPath);
        Assert.Contains("包正文", new SkillCatalog(_root).ReadContent("archify"));
    }

    [Fact]
    public void 同名目录包优先于扁平文件()
    {
        Write("archify.md", "扁平正文");
        WritePackage("archify", "---\ndescription: 包版本\n---\n包正文");

        var catalog = new SkillCatalog(_root);
        var skill = Assert.Single(catalog.List());

        Assert.Equal(SkillKind.Package, skill.Kind);
        Assert.Contains("包正文", catalog.ReadContent("archify"));
    }

    [Fact]
    public void 包内引用按相对路径读取()
    {
        WritePackage("archify", "---\ndescription: 画图\n---\n", ("references/delivery-contract.md", "错误码 E100"));

        var result = new SkillCatalog(_root).ReadFile("archify", "references\\delivery-contract.md");

        Assert.True(result.IsOk);
        Assert.Equal("references/delivery-contract.md", result.RelativePath);
        Assert.Contains("E100", result.Content);
    }

    [Fact]
    public void 包内路径越界一律拒绝()
    {
        Write("secret.md", "不该读到");
        WritePackage("archify", "---\ndescription: 画图\n---\n", ("references/ok.md", "ok"));

        var catalog = new SkillCatalog(_root);

        Assert.Equal(SkillFileStatus.InvalidPath, catalog.ReadFile("archify", "../secret.md").Status);
        Assert.Equal(SkillFileStatus.InvalidPath, catalog.ReadFile("archify", "references/a\u0001.md").Status);
        Assert.Equal(SkillFileStatus.InvalidPath, catalog.ReadFile("archify", Path.Combine(_root, "secret.md")).Status);
        Assert.Equal(SkillFileStatus.InvalidPath, catalog.ReadFile("archify", "references/../../secret.md").Status);
        Assert.Equal(SkillFileStatus.InvalidPath, catalog.ReadFile("archify", "C:\\Windows\\win.ini").Status);
        Assert.Equal(SkillFileStatus.InvalidPath, catalog.ReadFile("archify", "references/ok.md:stream").Status);
    }

    [Fact]
    public void 包内只允许文本引用扩展名()
    {
        WritePackage("archify", "---\ndescription: 画图\n---\n", ("logo.png", "binary"));

        Assert.Equal(
            SkillFileStatus.UnsupportedExtension,
            new SkillCatalog(_root).ReadFile("archify", "logo.png").Status);
    }

    [Fact]
    public void 包内文件不存在时返回_FileNotFound()
    {
        WritePackage("archify", "---\ndescription: 画图\n---\n");

        Assert.Equal(
            SkillFileStatus.FileNotFound,
            new SkillCatalog(_root).ReadFile("archify", "references/missing.md").Status);
    }

    [Fact]
    public void 扁平技能没有包内引用()
    {
        Write("总结.md", "正文");

        Assert.Equal(
            SkillFileStatus.FlatSkill,
            new SkillCatalog(_root).ReadFile("总结", "references/x.md").Status);
    }

    [Fact]
    public void 没有_SKILL_md_的目录与根目录_SKILL_md_都不算技能()
    {
        Directory.CreateDirectory(Path.Combine(_root, "not-a-skill"));
        File.WriteAllText(Path.Combine(_root, "not-a-skill", "readme.md"), "普通目录");
        Write("SKILL.md", "根目录清单");
        WritePackage("real", "---\ndescription: 真技能\n---\n");

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("real", skill.Name);
    }

    [Fact]
    public void skill_release_的版本与来源优先于_frontmatter()
    {
        WritePackage("archify", """
            ---
            description: 画图。
            version: 1.0
            ---
            """);

        File.WriteAllText(
            Path.Combine(_root, "archify", "skill-release.json"),
            """{"version":"3.2.0","source":{"repository":"https://github.com/tt-a1i/archify"}}""");

        var skill = Assert.Single(new SkillCatalog(_root).List());

        Assert.Equal("3.2.0", skill.Version);
        Assert.Equal("https://github.com/tt-a1i/archify", skill.Source);
    }

    [Fact]
    public void 快照统计文件数与内容哈希_内容变化后哈希变化()
    {
        WritePackage("archify", "---\ndescription: 画图\n---\n", ("bin/archify.mjs", "console.log(1)"));

        var catalog = new SkillCatalog(_root);
        var first = catalog.Snapshot("archify");

        Assert.NotNull(first);
        Assert.Equal(2, first!.FileCount);
        Assert.False(string.IsNullOrWhiteSpace(first.Hash));

        File.WriteAllText(Path.Combine(_root, "archify", "bin", "archify.mjs"), "console.log(22)");

        var second = catalog.Snapshot("archify");

        Assert.NotNull(second);
        Assert.NotEqual(first.Hash, second!.Hash);
        Assert.Equal(first.Bytes + 1, second.Bytes);
    }

    [Fact]
    public void 摘要包含版本与目录包标记()
    {
        WritePackage("archify", "---\ndescription: 画图\n---\n", ("skill-release.json", """{"version":"3.2.0"}"""));

        var summary = new SkillCatalog(_root).Summary("archify");

        Assert.NotNull(summary);
        Assert.Contains("v3.2.0", summary);
        Assert.Contains("目录包", summary);
        Assert.Contains("2 个文件", summary);
    }

    private void WritePackage(
        string name,
        string skill,
        params (string Path, string Content)[] files)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "SKILL.md"), skill);

        foreach (var (path, content) in files)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
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
