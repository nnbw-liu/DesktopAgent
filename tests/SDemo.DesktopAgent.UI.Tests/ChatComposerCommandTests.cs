using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// `/` 菜单：命令在前、技能在后；命令直接执行（不进模型），技能插入字面量并随提交带上技能名。
/// </summary>
public sealed class ChatComposerCommandTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private string? _command;
    private ComposerSubmission? _sent;

    private static readonly SkillInfo[] Skills =
    [
        new("translate", "把文本翻成目标语言"),
        new("重写", "整文件重写", DisableModelInvocation: true)
    ];

    public ChatComposerCommandTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new AttachmentStore(_temp.Root));
        Services.AddSingleton<IComposerReferenceIndex>(new EmptyReferences());
    }

    [Fact]
    public void 斜杠菜单先列命令再列技能()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(1);

        var cut = RenderComposer();

        cut.Find("textarea").Input("/");

        var labels = cut.FindAll(".composer-menu-label").Select(element => element.TextContent).ToArray();

        Assert.Equal("/compact", labels[0]);
        Assert.Contains("/translate", labels);
        Assert.Contains("/重写", labels);

        // 命令全部排在技能前面。
        var firstSkill = Array.IndexOf(labels, "/translate");
        Assert.All(
            ComposerCommands.All,
            command => Assert.True(Array.IndexOf(labels, "/" + command.Name) < firstSkill));
    }

    [Fact]
    public void 选命令直接执行且不发送消息()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(5);

        var cut = RenderComposer();

        cut.Find("textarea").Input("/comp");
        cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        Assert.Equal("/compact", _command);
        Assert.Null(_sent);
        Assert.Equal(string.Empty, cut.Instance.Text);
    }

    [Fact]
    public void 选技能插入字面量并关闭菜单()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(4);

        var cut = RenderComposer();

        cut.Find("textarea").Input("/tra");
        cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        Assert.Equal("/translate ", cut.Instance.Text);
        Assert.Empty(cut.FindAll(".composer-menu"));
        Assert.Null(_command);
    }

    [Fact]
    public void 手打技能名也能被认出来()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(15);

        var cut = RenderComposer();

        cut.Find("textarea").Input("/translate 你好");
        cut.InvokeAsync(() => cut.Instance.HandleEnterAsync());

        Assert.NotNull(_sent);
        Assert.Equal("translate", _sent!.InvokedSkill);
        Assert.Contains("/translate", _sent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 未知命令也交给宿主拒绝而不是发给模型()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(5);

        var cut = RenderComposer();

        cut.Find("textarea").Input("/nope");
        cut.InvokeAsync(() => cut.Instance.HandleEnterAsync());

        Assert.Equal("/nope", _command);
        Assert.Null(_sent);
    }

    [Fact]
    public void 高亮层把已落下的引用与命令包成token()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(0);

        var cut = RenderComposer();

        cut.Find("textarea").Input("/compact");

        var tokens = cut.FindAll(".composer-token").ToArray();
        Assert.Single(tokens);
        Assert.Contains("composer-token--command", tokens[0].ClassList);
    }

    private IRenderedComponent<ChatComposer> RenderComposer() =>
        RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "command-tests")
            .Add(component => component.Skills, Skills)
            .Add(component => component.WorkspaceRoot, _temp.Root)
            .Add(component => component.Text, string.Empty)
            .Add(component => component.OnCommand, (string line) => _command = line)
            .Add(component => component.OnSend, (ComposerSubmission submission) => _sent = submission));

    private sealed class EmptyReferences : IComposerReferenceIndex
    {
        public IReadOnlyList<string> List(string? rootPath) => [];

        public void Invalidate()
        {
        }
    }
}

