using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// <c>@</c> 补全：只给路径不给内容；目录继续下钻、文件插入后关菜单。
/// 键盘走 JS（document 捕获），这里直接调组件上的 <c>[JSInvokable]</c> 方法。
/// </summary>
public sealed class ChatComposerMentionTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly StubReferences _references = new();

    public ChatComposerMentionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new AttachmentStore(_temp.Root));
        Services.AddSingleton<IComposerReferenceIndex>(_references);
    }

    [Fact]
    public void 输入at后弹出候选并插入路径()
    {
        _references.Items = ["src/Program.cs", "src/Core/Budget.cs", "README.md"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(4);

        var cut = RenderComposer();

        cut.Find("textarea").Input("@Pro");

        Assert.Equal("@Pro", cut.Instance.Text);
        var rows = cut.FindAll(".composer-menu-row").ToArray();
        Assert.Single(rows);
        Assert.Contains("src/Program.cs", rows[0].TextContent, StringComparison.Ordinal);
        Assert.True(rows[0].ClassList.Contains("is-active"));

        cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        // 插入的是纯文本 mention，菜单随之关闭。
        Assert.Equal("@src/Program.cs ", cut.Instance.Text);
        Assert.Empty(cut.FindAll(".composer-menu"));
    }

    [Fact]
    public void 方向键在候选里循环移动()
    {
        _references.Items = ["a.cs", "b.cs", "c.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(1);

        var cut = RenderComposer();

        cut.Find("textarea").Input("@");

        cut.InvokeAsync(() => cut.Instance.MenuMoveAsync(1));
        Assert.True(cut.FindAll(".composer-menu-row").ElementAt(1).ClassList.Contains("is-active"));

        // 从第二项再往下：第三项；再往下回到第一项。
        cut.InvokeAsync(() => cut.Instance.MenuMoveAsync(1));
        cut.InvokeAsync(() => cut.Instance.MenuMoveAsync(1));
        Assert.True(cut.FindAll(".composer-menu-row").ElementAt(0).ClassList.Contains("is-active"));

        // 往上越界回到最后一项。
        cut.InvokeAsync(() => cut.Instance.MenuMoveAsync(-1));
        Assert.True(cut.FindAll(".composer-menu-row").ElementAt(2).ClassList.Contains("is-active"));
    }

    [Fact]
    public void 选目录后菜单保持打开继续下钻()
    {
        _references.Items = ["src/", "src/Program.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(3);

        var cut = RenderComposer();

        cut.Find("textarea").Input("@sr");

        cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        // 目录：补成 `@src/` 并保留菜单（下一次输入会重新算下一层）。
        Assert.Equal("@src/", cut.Instance.Text);
        Assert.NotEmpty(cut.FindAll(".composer-menu"));
    }

    [Fact]
    public void 没有触发符时不弹菜单()
    {
        _references.Items = ["src/Program.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(5);

        var cut = RenderComposer();

        cut.Find("textarea").Input("hello");

        Assert.Empty(cut.FindAll(".composer-menu"));
    }

    [Fact]
    public void 转义关闭菜单后不再显示()
    {
        _references.Items = ["src/Program.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(4);

        var cut = RenderComposer();
        cut.Find("textarea").Input("@Pro");
        Assert.NotEmpty(cut.FindAll(".composer-menu"));

        cut.InvokeAsync(() => cut.Instance.MenuDismissAsync());

        Assert.Empty(cut.FindAll(".composer-menu"));
    }

    private IRenderedComponent<ChatComposer> RenderComposer() =>
        RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "composer-test")
            .Add(component => component.WorkspaceRoot, _temp.Root)
            .Add(component => component.Text, string.Empty));

    /// <summary>固定候选：测试只关心交互，不关心真实扫盘。</summary>
    private sealed class StubReferences : IComposerReferenceIndex
    {
        public IReadOnlyList<string> Items { get; set; } = [];

        public IReadOnlyList<string> List(string? rootPath) => Items;

        public void Invalidate()
        {
        }
    }
}





