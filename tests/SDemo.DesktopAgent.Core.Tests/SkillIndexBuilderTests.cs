using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M2 技能索引：按输入挑 top N、渲染紧凑、20 个技能也不超过 400 token。</summary>
public sealed class SkillIndexBuilderTests
{
    [Fact]
    public void 关键词命中标签或何时用会被优先选中()
    {
        var entries = new[]
        {
            Entry("翻译", "翻译文本", "需要多语言输出时", ["translate"]),
            Entry("总结", "压缩长文", "长文需要一句话结论时", ["summary"]),
            Entry("复盘", "总结改动得失", "任务结束后", ["review"])
        };

        var selected = SkillIndexBuilder.Select(entries, "帮我把这篇长文总结一下", maxEntries: 2);

        Assert.Equal(2, selected.Count);
        Assert.Equal("总结", selected[0].Name);
    }

    [Fact]
    public void 没有命中时按文件顺序取前N条()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(index => Entry($"skill{index}", "说明", "用途", []))
            .ToArray();

        var selected = SkillIndexBuilder.Select(entries, "完全无关的输入", maxEntries: 3);

        Assert.Equal(["skill0", "skill1", "skill2"], selected.Select(entry => entry.Name));
    }

    [Fact]
    public void 渲染包含名字描述何时用与标签且标注手选技能()
    {
        var entries = new[]
        {
            Entry("总结", "一句话结论。", "长文需要压缩时", ["summary", "压缩"]),
            Entry("翻译", "翻译成目标语言。", "需要多语言输出时", [])
        };

        var text = SkillIndexBuilder.Render(entries, selectedSkill: "总结");

        Assert.Contains("## 可用技能（索引）", text);
        Assert.Contains("load_skill(name)", text);
        Assert.Contains("- 总结：一句话结论。（何时用：长文需要压缩时） [summary/压缩]", text);
        Assert.Contains("本轮已手选", text);
    }

    [Fact]
    public void 二十个技能的索引不超过四百token()
    {
        var counter = new HeuristicTokenCounter();
        var entries = Enumerable.Range(0, 20)
            .Select(index => Entry(
                $"技能{index}",
                $"这是第 {index} 个技能的说明，用来描述它在什么场景下有用，句子刻意写得长一点以接近真实技能。",
                $"当用户需要第 {index} 类任务时",
                ["tag1", "tag2", "tag3"]))
            .ToArray();

        var selected = SkillIndexBuilder.Select(entries, "帮我处理第 7 类任务", maxEntries: 5);
        var text = SkillIndexBuilder.Render(selected);

        Assert.True(counter.CountMessage("system", text) <= 400, $"索引实际 {counter.CountMessage("system", text)} token");
    }

    [Fact]
    public void 禁止模型调用的技能不进索引()
    {
        var entries = new[]
        {
            Entry("总结", "压缩长文", "长文需要结论时", []),
            new SkillIndexEntry("重写", "整文件重写", "用户明确要求重写时", [], DisableModelInvocation: true)
        };

        var selected = SkillIndexBuilder.Select(entries, "帮我重写这个文件", maxEntries: 5);

        Assert.DoesNotContain(selected, entry => entry.Name == "重写");
        Assert.Contains(selected, entry => entry.Name == "总结");
    }

    private static SkillIndexEntry Entry(
        string name,
        string description,
        string whenToUse,
        IReadOnlyList<string> tags) => new(name, description, whenToUse, tags);
}
