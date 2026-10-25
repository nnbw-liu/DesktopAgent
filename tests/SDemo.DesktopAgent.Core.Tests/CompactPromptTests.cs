using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P6 短提示词：长度、规则、技能截断。</summary>
public sealed class CompactPromptTests
{
    [Fact]
    public void 精简提示不超过二百五十token()
    {
        var counter = new HeuristicTokenCounter();

        Assert.True(counter.CountMessage("system", SystemPrompts.Compact) <= 250);
    }

    [Fact]
    public void 精简提示保留关键规则()
    {
        Assert.Contains("read_file", SystemPrompts.Compact);
        Assert.Contains("一次只调用一个工具", SystemPrompts.Compact);
        Assert.Contains("完成", SystemPrompts.Compact);
    }

    [Fact]
    public void 八K模型自动使用精简提示并截断技能()
    {
        var settings = new StubSettingsService();
        settings.Current.Agent.Mode = AgentModePreference.Auto;
        var provider = new ModelProvider
        {
            Id = "local",
            Models = [new ModelEntry { Id = "qwen3-4b", Profile = ModelProfilePresets.FromPreset("8K") }]
        };
        var builder = new ContextBuilder(new HeuristicTokenCounter(), settings);

        var plan = builder.Build(new ContextBuildRequest(
            provider,
            "qwen3-4b",
            [new ChatMessage(ChatMessageRole.User, "改代码")],
            SkillName: "demo",
            SkillText: new string('技', 4000)));

        Assert.Equal(SystemPrompts.Compact, plan.Instructions[..SystemPrompts.Compact.Length]);
        Assert.True(plan.Report.Segments.Skills <= 600);
    }

    [Fact]
    public void 大窗口模型使用完整提示()
    {
        var settings = new StubSettingsService();
        var provider = new ModelProvider
        {
            Id = "cloud",
            Models = [new ModelEntry { Id = "big", Profile = ModelProfilePresets.FromPreset("32K") }]
        };
        var builder = new ContextBuilder(new HeuristicTokenCounter(), settings);

        var plan = builder.Build(new ContextBuildRequest(
            provider,
            "big",
            [new ChatMessage(ChatMessageRole.User, "你好")]));

        Assert.Equal(SystemPrompts.FullFor(supportsReasoning: false), plan.Instructions);
    }

    [Fact]
    public void 有思考通道的大窗口模型不再要求PLAN()
    {
        // P1b §3.2.5：提示词按模型能力分叉，靠 ModelProfile.SupportsReasoning 决定。
        var settings = new StubSettingsService();
        var provider = new ModelProvider
        {
            Id = "cloud",
            Models =
            [
                new ModelEntry
                {
                    Id = "reasoner",
                    Profile = new ModelProfile(32_768, 4096, SupportsReasoning: true)
                }
            ]
        };
        var builder = new ContextBuilder(new HeuristicTokenCounter(), settings);

        var plan = builder.Build(new ContextBuildRequest(
            provider,
            "reasoner",
            [new ChatMessage(ChatMessageRole.User, "你好")]));

        Assert.Equal(SystemPrompts.FullWithReasoning, plan.Instructions);
        Assert.DoesNotContain("PLAN", plan.Instructions);
    }
}
