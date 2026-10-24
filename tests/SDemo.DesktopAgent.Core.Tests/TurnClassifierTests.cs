using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 「先决定做什么」：对话轮不暴露工具；任务轮才走选工具；只读疑问不能被误判成聊天或改文件。
/// </summary>
public sealed class TurnClassifierTests
{
    [Theory]
    [InlineData("你好")]
    [InlineData("您好，在吗")]
    [InlineData("嗯")]
    [InlineData("谢谢！")]
    [InlineData("你是谁")]
    [InlineData("讲个笑话")]
    [InlineData("不要用工具，直接回答我")]
    public void 寒暄与明确的只聊轮判为对话(string input)
    {
        Assert.Equal(TurnIntent.Chat, TurnClassifier.Classify(input));
        Assert.True(TurnClassifier.IsChat(input));
    }

    [Theory]
    [InlineData("帮我改一下这个文件")]
    [InlineData("修一下这个 bug")]
    [InlineData("给设置页加一个开关")]
    [InlineData("实现登录功能")]
    [InlineData("跑一下构建")]
    [InlineData("把设置页的开关改成默认打开")]
    public void 明确动手指令判为任务(string input)
    {
        Assert.Equal(TurnIntent.Task, TurnClassifier.Classify(input));
    }

    [Theory]
    [InlineData("解释一下怎么改这段配置")]
    [InlineData("这个文件改哪里了？")]
    [InlineData("看一下这个文件")]
    [InlineData("上次关于沙箱 ACL 的结论")]
    [InlineData("这个函数是干什么的")]
    public void 只读提问判为问答而不是任务(string input)
    {
        Assert.Equal(TurnIntent.Answer, TurnClassifier.Classify(input));
        Assert.False(TurnClassifier.HasImperativeAction(input));
    }

    [Fact]
    public void 寒暄后面跟任务时按任务处理()
    {
        Assert.Equal(TurnIntent.Task, TurnClassifier.Classify("你好，帮我改个文件"));
    }

    [Fact]
    public void 手选技能时不按聊天处理()
    {
        Assert.Equal(TurnIntent.Task, TurnClassifier.Classify("你好", hasSelectedSkill: true));
    }

    [Fact]
    public void 空输入按对话处理()
    {
        Assert.Equal(TurnIntent.Chat, TurnClassifier.Classify("   "));
    }
}
