using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M2：技能索引进入系统提示，受分段预算约束，手选技能仍全文注入。</summary>
public sealed class SkillIndexContextTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    [Fact]
    public void 索引进入系统提示且计入技能分段()
    {
        var index = "## 可用技能（索引）\n需要正文时调用 load_skill(name)。\n- 总结：压缩长文。\n";

        var plan = Builder().Build(new ContextBuildRequest(
            Provider("32K"),
            "m",
            [Message()],
            SkillIndexText: index));

        Assert.Contains("## 可用技能（索引）", plan.Instructions);
        Assert.Contains("load_skill(name)", plan.Instructions);
        Assert.True(plan.Report.Segments.Skills > 0);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
    }

    [Fact]
    public void 超长索引按预算截断()
    {
        var index = "## 可用技能（索引）\n" + new string('技', 5000);

        var plan = Builder().Build(new ContextBuildRequest(
            Provider("32K"),
            "m",
            [Message()],
            SkillIndexText: index));

        Assert.Contains("技能索引已截断", plan.Instructions);
        Assert.True(
            plan.Report.Segments.Skills <= plan.Budget.SkillIndexLimit + 20,
            $"技能段实际 {plan.Report.Segments.Skills} token");
    }

    [Fact]
    public void 索引与手选技能同时存在时都被注入()
    {
        var index = "## 可用技能（索引）\n- 总结：压缩长文。\n- 翻译：翻译文本。\n";
        var skill = new string('正', 2000);

        var plan = Builder().Build(new ContextBuildRequest(
            Provider("32K"),
            "m",
            [Message()],
            SkillName: "总结",
            SkillText: skill,
            SkillIndexText: index));

        Assert.Contains("## 可用技能（索引）", plan.Instructions);
        Assert.Contains("## 本轮使用的技能：总结", plan.Instructions);
        Assert.Contains(skill[..50], plan.Instructions);
        Assert.True(plan.Report.Segments.Skills > plan.Budget.SkillIndexLimit);
    }

    [Fact]
    public void Compact下索引加技能不超过六百token()
    {
        var index = "## 可用技能（索引）\n" + new string('技', 2000);

        var plan = Builder().Build(new ContextBuildRequest(
            Provider("8K"),
            "m",
            [Message()],
            SkillName: "demo",
            SkillText: new string('技', 4000),
            SkillIndexText: index));

        Assert.True(plan.Report.Segments.Skills <= 600, $"技能段实际 {plan.Report.Segments.Skills} token");
    }

    [Fact]
    public void 关闭索引时不注入()
    {
        var plan = Builder().Build(new ContextBuildRequest(
            Provider("32K"),
            "m",
            [Message()],
            SkillIndexText: null));

        Assert.DoesNotContain("## 可用技能（索引）", plan.Instructions);
        Assert.Equal(0, plan.Report.Segments.Skills);
    }

    private ContextBuilder Builder() => new(_counter, _settings);

    private static ChatMessage Message() => new(ChatMessageRole.User, "帮我总结一下这篇长文");

    private static ModelProvider Provider(string preset) => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset(preset)! }]
    };
}
