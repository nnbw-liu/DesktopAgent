using System.IO;
using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>随应用发布的内置插件必须能通过 M0 的全部校验（防止清单与 skill 走样）。</summary>
public sealed class BuiltinPluginTests
{
    [Fact]
    public void 内置translate插件可加载且三个入口都可用()
    {
        var repositoryRoot = FindRepositoryRoot();
        var builtinRoot = Path.Combine(repositoryRoot, "plugins");
        var localRoot = Path.Combine(repositoryRoot, "plugins", "__not-a-plugin-root__");

        var result = new PluginCatalog(builtinRoot, localRoot).Scan();

        Assert.Empty(result.Failures);
        var translate = Assert.Single(result.Usable);
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
        Assert.Equal(2, translate.Icon!.Paths.Count);
    }

    [Fact]
    public void 内置translate插件的system提示词已渲染()
    {
        var repositoryRoot = FindRepositoryRoot();
        var result = new PluginCatalog(
            Path.Combine(repositoryRoot, "plugins"),
            Path.Combine(repositoryRoot, "plugins", "__not-a-plugin-root__")).Scan();

        var translate = result.Find("translate");
        Assert.NotNull(translate);

        var capability = Assert.Single(translate!.Capabilities);
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" };
        var prompt = new PluginPromptComposer().BuildSystemPrompt(capability, values);

        Assert.Contains("日本語（ja）", prompt);
        Assert.DoesNotContain("---", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SDemo.DesktopAgent.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
