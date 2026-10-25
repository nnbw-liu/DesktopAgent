using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 输入框触发符的语法：`@` 只认行首 / 空白之后，`/` 只认行首；
/// `@"..."` 允许空格。规则先在契约层钉死，UI 只负责把菜单画出来。
/// </summary>
public sealed class ComposerTriggerTests
{
    [Fact]
    public void 行首的at是引用且查询为空()
    {
        var trigger = ComposerTriggers.Detect("@", 1);

        Assert.NotNull(trigger);
        Assert.Equal(ComposerTriggerKind.Mention, trigger!.Kind);
        Assert.Equal(string.Empty, trigger.Query);
        Assert.Equal(0, trigger.Start);
        Assert.Equal(1, trigger.Caret);
        Assert.False(trigger.Quoted);
    }

    [Fact]
    public void 空白之后的at是引用()
    {
        var text = "看看 @src/Pro";
        var trigger = ComposerTriggers.Detect(text, text.Length);

        Assert.NotNull(trigger);
        Assert.Equal("src/Pro", trigger!.Query);
        Assert.Equal(3, trigger.Start);
    }

    [Fact]
    public void 词中的at不触发()
    {
        Assert.Null(ComposerTriggers.Detect("foo@bar.com", 11));
        Assert.Null(ComposerTriggers.Detect("a@b", 3));
    }

    [Fact]
    public void 引号引用允许空格()
    {
        var text = "@\"my file";
        var trigger = ComposerTriggers.Detect(text, text.Length);

        Assert.NotNull(trigger);
        Assert.True(trigger!.Quoted);
        Assert.Equal("my file", trigger.Query);
        Assert.Equal(0, trigger.Start);
    }

    [Fact]
    public void 引号闭合后菜单关闭()
    {
        Assert.Null(ComposerTriggers.Detect("@\"my file\"", 10));
    }

    [Fact]
    public void 行首斜杠是命令()
    {
        var trigger = ComposerTriggers.Detect("/comp", 5);

        Assert.NotNull(trigger);
        Assert.Equal(ComposerTriggerKind.Command, trigger!.Kind);
        Assert.Equal("comp", trigger.Query);
    }

    [Theory]
    [InlineData("和/或")]
    [InlineData("a /b")]
    [InlineData("路径是 src/bin")]
    public void 词中或行中的斜杠不触发(string text)
    {
        Assert.Null(ComposerTriggers.Detect(text, text.Length));
    }

    [Fact]
    public void 换行之后的斜杠重新算行首()
    {
        var text = "第一行\n/ski";
        var trigger = ComposerTriggers.Detect(text, text.Length);

        Assert.NotNull(trigger);
        Assert.Equal(ComposerTriggerKind.Command, trigger!.Kind);
        Assert.Equal(4, trigger.Start);
    }

    [Fact]
    public void 触发符后面的空格结束引用()
    {
        Assert.Null(ComposerTriggers.Detect("@src Program", 11));
    }

    [Fact]
    public void 光标在触发符之前时不算()
    {
        Assert.Null(ComposerTriggers.Detect("@abc", 0));
    }

    [Theory]
    [InlineData("README.md", "@README.md")]
    [InlineData("src/Program.cs", "@src/Program.cs")]
    [InlineData("my docs/a.md", "@\"my docs/a.md\"")]
    public void 引用按有无空格决定加不加引号(string path, string expected)
    {
        Assert.Equal(expected, ComposerTriggers.FormatMention(path));
    }

    [Fact]
    public void 目录下钻保留未闭合引号并补尾斜杠()
    {
        Assert.Equal("@src/", ComposerTriggers.FormatDirectoryMention("src"));
        Assert.Equal("@src/docs/", ComposerTriggers.FormatDirectoryMention("src/docs/"));
        Assert.Equal("@\"my docs/", ComposerTriggers.FormatDirectoryMention("my docs"));
    }

    [Fact]
    public void 反斜杠统一成正斜杠()
    {
        Assert.Equal("@src/Program.cs", ComposerTriggers.FormatMention(@"src\Program.cs"));
    }
}
