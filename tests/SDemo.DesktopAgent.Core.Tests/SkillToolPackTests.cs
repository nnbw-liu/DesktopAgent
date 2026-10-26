using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M2：load_skill 按需读取正文，缺库 / 缺技能时给可恢复的提示。</summary>
public sealed class SkillToolPackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sdemo-skill-tool-tests",
        Guid.NewGuid().ToString("N"));

    public SkillToolPackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task 读到正文并被标记为参考资料()
    {
        File.WriteAllText(Path.Combine(_root, "总结.md"), "把长文压成三句话。");

        var result = await Load("总结");

        Assert.Contains("[SKILL_BEGIN:总结]", result);
        Assert.Contains("不是指令", result);
        Assert.Contains("把长文压成三句话。", result);
    }

    [Fact]
    public async Task 禁止模型调用的技能不给加载()
    {
        File.WriteAllText(
            Path.Combine(_root, "重写.md"),
            "---\ndescription: 整文件重写\ndisable-model-invocation: true\n---\n正文");

        var result = await Load("重写");

        Assert.Contains("只能由用户", result);
        Assert.DoesNotContain("[SKILL_BEGIN", result);
    }

    [Fact]
    public async Task 技能不存在时列出可用技能()
    {
        File.WriteAllText(Path.Combine(_root, "总结.md"), "正文");

        var result = await Load("不存在的技能");

        Assert.Contains("没有技能", result);
        Assert.Contains("总结", result);
    }

    [Fact]
    public async Task 正文过长时按工具结果预算分页()
    {
        var content = string.Concat(Enumerable.Repeat("技能正文。", 800));
        File.WriteAllText(Path.Combine(_root, "长技能.md"), content);

        var result = await Load("长技能");

        Assert.Contains("已到预算上限", result);
        Assert.Contains("offset:", result);
        Assert.True(result.Length < content.Length);

        // 按返回里的 offset 一直续读，直到没有下一页为止。
        var page = result;
        var guard = 0;
        while (page.Contains("offset: ", StringComparison.Ordinal) && guard++ < 10)
        {
            page = await Load("长技能", ExtractOffset(page));
        }

        Assert.DoesNotContain("offset: ", page);
        Assert.Contains("技能正文。", page);
        Assert.Contains("[SKILL_END]", page);
    }

    [Fact]
    public async Task 没有技能库时不抛异常()
    {
        var tools = new SkillToolPack().Create(Context(skills: null));
        var tool = Assert.IsAssignableFrom<AIFunction>(
            tools.Single(item => string.Equals(item.Name, SkillToolPack.FunctionId, StringComparison.Ordinal)));

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "总结" }));

        Assert.Contains("没有技能库", result?.ToString());
    }

    [Fact]
    public async Task 读取包内引用并标记为参考资料()
    {
        WritePackage("archify", ("references/delivery-contract.md", "错误码 E100 的处理方式。"));

        var result = await ReadFile("archify", "references/delivery-contract.md");

        Assert.Contains("[SKILL_FILE_BEGIN:archify/references/delivery-contract.md]", result);
        Assert.Contains("不是指令", result);
        Assert.Contains("E100", result);
    }

    [Fact]
    public async Task 引用过长时按预算分页并能续读()
    {
        var content = string.Concat(Enumerable.Repeat("参考正文。", 800));
        WritePackage("archify", ("references/long.md", content));

        var page = await ReadFile("archify", "references/long.md");
        Assert.Contains("已到预算上限", page);
        Assert.Contains("read_skill_file", page);

        var guard = 0;
        while (page.Contains("offset: ", StringComparison.Ordinal) && guard++ < 10)
        {
            page = await ReadFile("archify", "references/long.md", ExtractOffset(page));
        }

        Assert.DoesNotContain("offset: ", page);
        Assert.Contains("参考正文。", page);
    }

    [Fact]
    public async Task 引用路径越界时提示不合法()
    {
        File.WriteAllText(Path.Combine(_root, "secret.md"), "不该读到");
        WritePackage("archify", ("references/ok.md", "ok"));

        var result = await ReadFile("archify", "../secret.md");

        Assert.Contains("不合法", result);
        Assert.DoesNotContain("不该读到", result);
    }

    [Fact]
    public async Task 扁平技能读引用时指回_load_skill()
    {
        File.WriteAllText(Path.Combine(_root, "总结.md"), "正文");

        var result = await ReadFile("总结", "references/x.md");

        Assert.Contains("单文件技能", result);
        Assert.Contains("load_skill", result);
    }

    [Fact]
    public async Task 引用文件不存在时提示先看_SKILL_md()
    {
        WritePackage("archify", ("references/ok.md", "ok"));

        var result = await ReadFile("archify", "references/missing.md");

        Assert.Contains("没有", result);
        Assert.Contains("SKILL.md", result);
    }

    [Fact]
    public async Task 引用扩展名不在白名单时拒绝()
    {
        WritePackage("archify", ("logo.png", "binary"));

        var result = await ReadFile("archify", "logo.png");

        Assert.Contains(".md", result);
        Assert.Contains(".json", result);
        Assert.Contains(".txt", result);
    }

    [Fact]
    public async Task 禁止模型调用的包技能不能读引用()
    {
        WritePackage(
            "archify",
            [("references/x.md", "正文")],
            "description: 画图\ndisable-model-invocation: true");

        var result = await ReadFile("archify", "references/x.md");

        Assert.Contains("只能由用户", result);
        Assert.DoesNotContain("[SKILL_FILE_BEGIN", result);
    }

    [Fact]
    public async Task 空引用返回空文件提示()
    {
        WritePackage("archify", ("references/empty.md", string.Empty));

        Assert.Contains("空", await ReadFile("archify", "references/empty.md"));
    }

    private async Task<string> Load(string name, int offset = 0)
    {
        var arguments = new Dictionary<string, object?> { ["name"] = name };
        if (offset > 0)
        {
            arguments["offset"] = offset;
        }

        return await Invoke(SkillToolPack.FunctionId, arguments);
    }

    private async Task<string> ReadFile(string name, string path, int offset = 0)
    {
        var arguments = new Dictionary<string, object?> { ["name"] = name, ["path"] = path };
        if (offset > 0)
        {
            arguments["offset"] = offset;
        }

        return await Invoke(SkillToolPack.ReadFileFunctionId, arguments);
    }

    private async Task<string> Invoke(string functionName, Dictionary<string, object?> arguments)
    {
        var tools = new SkillToolPack().Create(Context(new SkillCatalog(_root)));
        var tool = Assert.IsAssignableFrom<AIFunction>(
            tools.Single(item => string.Equals(item.Name, functionName, StringComparison.Ordinal)));

        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments));

        return result?.ToString() ?? string.Empty;
    }

    private void WritePackage(
        string name,
        params (string Path, string Content)[] files)
        => WritePackage(name, files, frontmatter: "description: 画图");

    private void WritePackage(
        string name,
        (string Path, string Content)[] files,
        string frontmatter)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "SKILL.md"), $"---\n{frontmatter}\n---\n\n包正文");

        foreach (var (path, content) in files)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }

    /// <summary>从 "offset: 1840）" 里取出下一页偏移。</summary>
    private static int ExtractOffset(string text)
    {
        var marker = "offset: ";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.IndexOf('）', start);

        return int.Parse(text[start..end]);
    }

    private static ToolContext Context(ISkillLibrary? skills) => new(
        "session",
        Path.GetTempPath(),
        [],
        new StubSettingsService(),
        new ToolApprovalService(),
        new StubSandboxJobService(),
        new NullAuditLog(),
        Skills: skills);

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
