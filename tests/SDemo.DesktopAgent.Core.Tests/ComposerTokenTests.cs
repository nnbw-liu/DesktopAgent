using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 输入框里"已经落下"的 token：高亮着色、以及发送时认出被点名的技能。
/// 与 ComposerTriggers（正在输入什么）互补。
/// </summary>
public sealed class ComposerTokenTests
{
    [Fact]
    public void 扫出引用与命令两类token()
    {
        var tokens = ComposerTokens.Scan("@README.md 看一下\n/compact");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(ComposerTokenKind.Mention, tokens[0].Kind);
        Assert.Equal("@README.md", tokens[0].Text);
        Assert.Equal(0, tokens[0].Start);

        Assert.Equal(ComposerTokenKind.Command, tokens[1].Kind);
        Assert.Equal("/compact", tokens[1].Text);
    }

    [Fact]
    public void 邮箱与词中的斜杠不算token()
    {
        Assert.Empty(ComposerTokens.Scan("foo@bar.com 和/或"));
    }

    [Fact]
    public void 引号引用整段算一个token()
    {
        var tokens = ComposerTokens.Scan("看看 @\"my file.cs\" 谢谢");

        var token = Assert.Single(tokens);
        Assert.Equal("@\"my file.cs\"", token.Text);
    }

    [Fact]
    public void 换行后的斜杠算命令()
    {
        var tokens = ComposerTokens.Scan("先做这个\n/summarize 然后");

        var token = Assert.Single(tokens);
        Assert.Equal(ComposerTokenKind.Command, token.Kind);
        Assert.Equal("/summarize", token.Text);
    }

    [Fact]
    public void 高亮html转义并给token包span()
    {
        var html = ComposerTokens.ToHighlightHtml("@a<b 和\n/compact");

        Assert.Contains("&lt;", html, StringComparison.Ordinal);
        Assert.Contains("composer-token--mention", html, StringComparison.Ordinal);
        Assert.Contains("composer-token--command", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 空文本给空高亮()
    {
        Assert.Equal(string.Empty, ComposerTokens.ToHighlightHtml(null));
        Assert.Equal(string.Empty, ComposerTokens.ToHighlightHtml(string.Empty));
    }

    [Fact]
    public void 认出被点名的技能()
    {
        var known = new[] { "translate", "总结" };

        Assert.Equal("translate", ComposerTokens.FindInvokedSkill("/translate 你好", known));
        Assert.Equal("总结", ComposerTokens.FindInvokedSkill("先 /总结 再说", known));
        Assert.Null(ComposerTokens.FindInvokedSkill("/unknown 你好", known));
        Assert.Null(ComposerTokens.FindInvokedSkill("普通一句话", known));
    }

    [Fact]
    public void 命令整行解析()
    {
        Assert.True(ComposerCommands.TryParseLine("/compact", out var name, out var input));
        Assert.Equal("compact", name);
        Assert.Equal(string.Empty, input);

        Assert.True(ComposerCommands.TryParseLine("  /compact  现在  ", out name, out input));
        Assert.Equal("compact", name);
        Assert.Equal("现在", input);

        Assert.True(ComposerCommands.TryParseLine("/my-cmd_v2 x", out name, out _));
        Assert.Equal("my-cmd_v2", name);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("hello")]
    [InlineData("/ compact")]
    [InlineData("/a/b")]
    public void 不合法的命令行被拒绝(string text)
    {
        Assert.False(ComposerCommands.TryParseLine(text, out _, out _));
    }

    [Fact]
    public void 命令目录大小写不敏感且能找到()
    {
        Assert.True(ComposerCommands.IsKnown("COMPACT"));
        Assert.False(ComposerCommands.IsKnown("nope"));
        Assert.Equal("压缩当前会话的历史", ComposerCommands.Find("compact")!.Description);
        Assert.Null(ComposerCommands.Find("nope"));
    }
}

