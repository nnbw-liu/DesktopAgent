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
        var tool = Assert.IsAssignableFrom<AIFunction>(
            new SkillToolPack().Create(Context(skills: null)).Single());

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "总结" }));

        Assert.Contains("没有技能库", result?.ToString());
    }

    private async Task<string> Load(string name, int offset = 0)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            new SkillToolPack().Create(Context(new SkillCatalog(_root))).Single());

        var arguments = new Dictionary<string, object?> { ["name"] = name };
        if (offset > 0)
        {
            arguments["offset"] = offset;
        }

        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments));

        return result?.ToString() ?? string.Empty;
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
