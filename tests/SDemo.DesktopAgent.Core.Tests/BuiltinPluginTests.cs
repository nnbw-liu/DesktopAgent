using System.IO;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Plugins.Summarize;
using SDemo.DesktopAgent.Plugins.Translate;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>随应用发布的内置插件必须能通过 M0 的全部校验（防止清单与 skill 走样）。</summary>
public sealed class BuiltinPluginTests
{
    [Fact]
    public void 内置translate插件可加载且三个入口都可用()
    {
        var result = ScanBuiltins();

        Assert.Empty(result.Failures);
        var translate = Assert.Single(result.Usable, plugin => plugin.Id == "translate");
        Assert.Equal("translate", translate.Id);
        Assert.Equal(PluginSource.Builtin, translate.Source);
        Assert.Equal("1.0.0", translate.Version);
        Assert.False(translate.HasErrors);

        Assert.Single(translate.Capabilities);
        Assert.Equal(3, translate.Mounts.Count);
        Assert.Single(translate.UsableMounts(PluginSurface.Sidebar));
        Assert.Single(translate.UsableMounts(PluginSurface.Composer));
        Assert.Single(translate.UsableMounts(PluginSurface.Message));

        var capability = Assert.Single(translate.Capabilities);
        Assert.True(capability.IsUsable);
        Assert.Equal(PluginOutputFormat.Segments, capability.OutputFormat);
        Assert.True(capability.SendsText);
        Assert.False(capability.AllowGlobalSkills);
        Assert.False(capability.AllowHistory);
        Assert.Contains("{{target.label}}", capability.SkillContent);

        var target = Assert.Single(capability.Parameters);
        Assert.Equal("target", target.Key);
        Assert.Equal("system-language", target.DefaultFrom);
        Assert.Equal("en", target.Fallback);
        Assert.Equal(6, target.Options.Count);
        Assert.Contains(target.Options, option => option.Value == "ja" && option.Label == "日本語");

        Assert.NotNull(translate.Icon);
        // 自左向右的「文 A」：4 条笔划 + 字母 A 的两笔；图标铺满 24px 画布。
        Assert.Equal(6, translate.Icon!.Paths.Count);
        Assert.Contains(translate.Icon.Paths, path => path.Contains("M2 5h12", StringComparison.Ordinal));
    }

    [Fact]
    public void 内置summarize插件可加载且三个入口都可用()
    {
        var result = ScanBuiltins();

        Assert.Empty(result.Failures);
        var summarize = Assert.Single(result.Usable, plugin => plugin.Id == "summarize");
        Assert.Equal(PluginSource.Builtin, summarize.Source);
        Assert.Equal("1.0.0", summarize.Version);
        Assert.False(summarize.HasErrors);
        Assert.Empty(summarize.Issues);

        Assert.Single(summarize.Capabilities);
        Assert.Equal(3, summarize.Mounts.Count);
        Assert.Single(summarize.UsableMounts(PluginSurface.Sidebar));
        Assert.Single(summarize.UsableMounts(PluginSurface.Composer));
        Assert.Single(summarize.UsableMounts(PluginSurface.Message));

        var capability = Assert.Single(summarize.Capabilities);
        Assert.True(capability.IsUsable);
        Assert.Equal(PluginOutputFormat.Text, capability.OutputFormat);
        Assert.True(capability.SendsText);
        Assert.False(capability.AllowGlobalSkills);
        Assert.False(capability.AllowHistory);
        Assert.Empty(capability.Parameters);
        Assert.Empty(capability.Issues);
        Assert.DoesNotContain("{{", capability.SkillContent);

        Assert.NotNull(summarize.Icon);
        Assert.Equal(3, summarize.Icon!.Paths.Count);
    }

    [Fact]
    public void 内置translate插件的system提示词已渲染()
    {
        var translate = ScanBuiltins().Find("translate");
        Assert.NotNull(translate);

        var capability = Assert.Single(translate!.Capabilities);
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" };
        var prompt = new PluginPromptComposer().BuildSystemPrompt(capability, values);

        Assert.Contains("日本語（ja）", prompt);
        Assert.DoesNotContain("---", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void 内置summarize插件的system提示词已渲染()
    {
        var summarize = ScanBuiltins().Find("summarize");
        Assert.NotNull(summarize);

        var capability = Assert.Single(summarize!.Capabilities);
        var prompt = new PluginPromptComposer().BuildSystemPrompt(
            capability,
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Contains("总结", prompt);
        Assert.DoesNotContain("---", prompt);
        Assert.DoesNotContain("{{", prompt);
        Assert.DoesNotContain("<s>", prompt);
        Assert.DoesNotContain("先原文后译文", prompt);
    }

    /// <summary>内置插件来自类库的嵌入资源，不再依赖输出目录里的 plugins\。</summary>
    private static PluginLoadResult ScanBuiltins() =>
        new PluginCatalog(
            [new TranslatePlugin(), new SummarizePlugin()],
            Path.Combine(Path.GetTempPath(), "__sdemo-no-local-plugins__")).Scan();
}
