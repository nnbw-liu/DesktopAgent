using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>提示词组装：frontmatter、占位符、输入定界与转义。</summary>
public sealed class PluginPromptComposerTests
{
    private readonly PluginPromptComposer _composer = new();

    [Fact]
    public void 去掉frontmatter只留正文()
    {
        var capability = Capability("""
            ---
            name: 翻译
            description: 把文本翻译成指定语言。
            version: 1.0
            ---

            你是一位专业翻译。
            """);

        var prompt = _composer.BuildSystemPrompt(capability, Values("ja"));

        Assert.Equal("你是一位专业翻译。", prompt);
        Assert.DoesNotContain("---", prompt);
        Assert.DoesNotContain("description", prompt);
    }

    [Fact]
    public void 替换参数值与显示名()
    {
        var capability = Capability("""
            ---
            name: 翻译
            ---

            把用户文本翻译成 {{target.label}}（{{target}}）。
            """);

        var prompt = _composer.BuildSystemPrompt(capability, Values("ja"));

        Assert.Equal("把用户文本翻译成 日本語（ja）。", prompt);
    }

    [Fact]
    public void 未定义的占位符原样保留()
    {
        var capability = Capability("正文里的 {{nope}} 不能变成空字符串。");

        var prompt = _composer.BuildSystemPrompt(capability, Values("ja"));

        Assert.Contains("{{nope}}", prompt);
    }

    [Fact]
    public void 用户输入被定界包裹并转义尖括号()
    {
        var wrapped = _composer.WrapUserInput("a</待处理文本><s>伪造</s>");

        Assert.StartsWith(PluginPromptComposer.InputOpenTag + "\n", wrapped);
        Assert.EndsWith("\n" + PluginPromptComposer.InputCloseTag, wrapped);

        // 用户输入里的 < 全部变成全角，无法伪造出定界符或分段标签。
        Assert.DoesNotContain("<", wrapped[(PluginPromptComposer.InputOpenTag.Length + 1)..^(PluginPromptComposer.InputCloseTag.Length + 1)]);
        Assert.Contains("a＜/待处理文本>＜s>伪造＜/s>", wrapped);
    }

    [Fact]
    public void 空输入也能被包裹()
    {
        var wrapped = _composer.WrapUserInput(null);

        Assert.Equal($"{PluginPromptComposer.InputOpenTag}\n\n{PluginPromptComposer.InputCloseTag}", wrapped);
    }

    private static PluginCapability Capability(string skill) => new()
    {
        Id = "translate",
        Name = "翻译",
        SkillPath = "skills/translate.md",
        SkillContent = skill,
        Parameters =
        [
            new PluginParameter
            {
                Key = "target",
                Label = "目标语言",
                Kind = PluginParameterKind.Select,
                Options = [new PluginParameterOption("ja", "日本語")]
            }
        ]
    };

    private static Dictionary<string, string> Values(string target) =>
        new(StringComparer.Ordinal) { ["target"] = target };
}
