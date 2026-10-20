using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>领域模型的展示与比较逻辑。</summary>
public sealed class PluginModelTests
{
    [Fact]
    public void 问题展示文件与行号()
    {
        var issue = new PluginIssue(PluginIssueSeverity.Error, "缺少 id", "plugin.json", 12);

        Assert.Equal("plugin.json 第 12 行：缺少 id", issue.ToDisplayString());
    }

    [Fact]
    public void 问题展示文件与JSON路径()
    {
        var issue = new PluginIssue(
            PluginIssueSeverity.Error,
            "指向不存在的能力",
            "plugin.json",
            Path: "mounts[0].capability");

        Assert.Equal("plugin.json › mounts[0].capability：指向不存在的能力", issue.ToDisplayString());
    }

    [Fact]
    public void 没有位置时只显示消息()
    {
        var issue = new PluginIssue(PluginIssueSeverity.Error, "插件目录无法读取");

        Assert.Equal("插件目录无法读取", issue.ToDisplayString());
    }

    [Fact]
    public void 参数显示名回落到原值()
    {
        var parameter = new PluginParameter
        {
            Key = "target",
            Label = "目标语言",
            Kind = PluginParameterKind.Select,
            Options = [new PluginParameterOption("ja", "日本語")]
        };

        Assert.Equal("日本語", parameter.ResolveLabel("ja"));
        Assert.Equal("de", parameter.ResolveLabel("de"));
        Assert.Equal("目标语言", parameter.ResolveLabel(null));
        Assert.True(parameter.HasOptions);
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("10.20.30+build", 10, 20, 30)]
    public void 解析合法宿主版本(string text, int major, int minor, int build)
    {
        Assert.True(PluginHostVersion.TryParse(text, out var version));
        Assert.Equal(new PluginHostVersion(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.x")]
    [InlineData("1.2.3.4.5")]
    public void 拒绝非法宿主版本(string text) =>
        Assert.False(PluginHostVersion.TryParse(text, out _));

    [Fact]
    public void 宿主版本按主次修订比较()
    {
        Assert.True(PluginHostVersion.TryParse("1.10.0", out var newer));
        Assert.True(PluginHostVersion.TryParse("1.9.9", out var older));

        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(older.CompareTo(newer) < 0);
        Assert.Equal(0, newer.CompareTo(new PluginHostVersion(1, 10, 0)));
    }
}
