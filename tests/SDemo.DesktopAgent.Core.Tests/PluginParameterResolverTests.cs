using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>参数解析：记忆 → default → defaultFrom → fallback → 第一个 option。</summary>
public sealed class PluginParameterResolverTests
{
    private readonly PluginParameterResolver _resolver = new();

    [Fact]
    public void 会话记忆优先于默认值()
    {
        var parameter = SelectParameter(defaultValue: "en");
        var memory = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PluginParameterResolver.MemoryKey("translate", "composer", "target")] = "ja"
        };

        Assert.Equal("ja", _resolver.Resolve(parameter, "translate", "composer", memory));
    }

    [Fact]
    public void 记忆值不在选项里_回落到默认值()
    {
        var parameter = SelectParameter(defaultValue: "en");
        var memory = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PluginParameterResolver.MemoryKey("translate", "composer", "target")] = "de"
        };

        Assert.Equal("en", _resolver.Resolve(parameter, "translate", "composer", memory));
    }

    [Fact]
    public void 没有默认值也没有记忆_取第一个option()
    {
        var parameter = SelectParameter(defaultValue: null);

        Assert.Equal("zh-Hans", _resolver.Resolve(parameter, "translate", "composer", EmptyMemory()));
    }

    [Fact]
    public void defaultFrom解析不出来时用fallback()
    {
        var parameter = new PluginParameter
        {
            Key = "target",
            Label = "目标语言",
            Kind = PluginParameterKind.Select,
            DefaultFrom = "system-language",
            Fallback = "en",
            Options =
            [
                new PluginParameterOption("zh-Hans", "简体中文"),
                new PluginParameterOption("en", "English")
            ]
        };

        // 无论当前系统语言是什么，结果都必须落在 options 里：要么系统语言命中，要么 fallback。
        var value = _resolver.Resolve(parameter, "translate", "composer", EmptyMemory());
        Assert.Contains(value, parameter.Options.Select(option => option.Value));
    }

    [Fact]
    public void 文本参数没有默认值时为空()
    {
        var parameter = new PluginParameter
        {
            Key = "note",
            Label = "备注",
            Kind = PluginParameterKind.Text,
            Required = false
        };

        Assert.Equal(string.Empty, _resolver.Resolve(parameter, "demo", "composer", EmptyMemory()));
    }

    [Fact]
    public void 开关参数默认false()
    {
        var parameter = new PluginParameter
        {
            Key = "keep",
            Label = "保留格式",
            Kind = PluginParameterKind.Toggle
        };

        Assert.Equal("false", _resolver.Resolve(parameter, "demo", "composer", EmptyMemory()));
    }

    [Fact]
    public void 解析全部参数()
    {
        var capability = new PluginCapability
        {
            Id = "demo",
            Name = "演示",
            SkillPath = "skills/main.md",
            Parameters = [SelectParameter(defaultValue: "en")]
        };

        var values = _resolver.Resolve(capability, "demo", "composer", EmptyMemory());

        Assert.Equal("en", Assert.Contains("target", values));
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant-US", "zh-Hant")]
    [InlineData("ja-JP", "ja")]
    [InlineData("fr-FR", "fr")]
    [InlineData("ko-KR", "ko")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("", "en")]
    public void 系统语言映射(string culture, string expected) =>
        Assert.Equal(expected, PluginParameterResolver.MapSystemLanguage(culture));

    [Fact]
    public void 记忆键格式()
    {
        Assert.Equal("translate/composer/target", PluginParameterResolver.MemoryKey("translate", "composer", "target"));
        Assert.Equal("ui.mode", PluginParameterResolver.OutputModeKey);
    }

    private static PluginParameter SelectParameter(string? defaultValue) => new()
    {
        Key = "target",
        Label = "目标语言",
        Kind = PluginParameterKind.Select,
        Default = defaultValue,
        Options =
        [
            new PluginParameterOption("zh-Hans", "简体中文"),
            new PluginParameterOption("ja", "日本語"),
            new PluginParameterOption("en", "English")
        ]
    };

    private static Dictionary<string, string> EmptyMemory() => new(StringComparer.Ordinal);
}
