using Bunit;
using Microsoft.AspNetCore.Components.Web;
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
    public async Task 选目录后菜单保持打开并立刻列出下一层()
    {
        _references.Items = ["src/", "src/Program.cs", "src/Core/Budget.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(3);

        var cut = RenderComposer();

        cut.Find("textarea").Input("@sr");

        await cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        // 目录：补成 `@src/` 并保留菜单，而且**马上**列出下一层（不依赖用户再敲一个字）。
        Assert.Equal("@src/", cut.Instance.Text);
        var labels = cut.FindAll(".composer-menu-label").Select(element => element.TextContent).ToArray();
        Assert.NotEmpty(labels);
        Assert.Contains("src/Program.cs", labels);
    }

    [Fact]
    public async Task 确认候选只插入一次文本_光标落在插入之后()
    {
        // 用户报的缺陷：Tab/Enter 选中 `物业交割单.docx ` 之后，高亮区域后面多出一截同样的文件名。
        // 根因是 JS 在 Blazor 已经渲染过的新文本上又按 [start, end) 插了一遍。
        _references.Items = ["物业交割单.docx"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(1);

        string? bound = null;
        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "composer-test")
            .Add(component => component.WorkspaceRoot, _temp.Root)
            .Add(component => component.Text, string.Empty)
            .Add(component => component.TextChanged, (string value) => bound = value));

        cut.Find("textarea").Input("@");
        await cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        const string expected = "@物业交割单.docx ";
        Assert.Equal(expected, cut.Instance.Text);
        Assert.Equal(expected, bound);

        // 文本只有一条写入路径：宿主算好后交给 applyText（顺带摆光标），
        // 没有第二条「按区间再插一次」的互操作。
        Assert.DoesNotContain(
            JSInterop.Invocations,
            invocation => invocation.Identifier == "sdComposer.replaceRange");

        var apply = Assert.Single(
            JSInterop.Invocations,
            invocation => invocation.Identifier == "sdComposer.applyText");
        Assert.Equal(expected, apply.Arguments[1]);
        Assert.Equal(expected.Length, (int)apply.Arguments[2]!);
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

    [Fact]
    public async Task 鼠标点选候选后插入路径_关菜单_并把焦点还给输入框()
    {
        _references.Items = ["src/Program.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(4);

        var cut = RenderComposer();

        cut.Find("textarea").Input("@Pro");

        // 走真实 DOM 事件（onclick），而不是直接调组件方法：鼠标点选就是这条路径。
        var row = cut.Find(".composer-menu-row");
        await cut.InvokeAsync(() => row.TriggerEventAsync("onclick", new MouseEventArgs()));

        Assert.Equal("@src/Program.cs ", cut.Instance.Text);
        Assert.Empty(cut.FindAll(".composer-menu"));

        // 鼠标点行会把焦点给到按钮（行移除后落到 body）：必须还回去，否则 Enter 不再发送。
        JSInterop.VerifyInvoke("sdComposer.focusInput");
    }

    [Fact]
    public void 候选行不进入Tab顺序_避免焦点被行抢走()
    {
        _references.Items = ["src/Program.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(1);

        var cut = RenderComposer();
        cut.Find("textarea").Input("@");

        Assert.Equal("-1", cut.Find(".composer-menu-row").GetAttribute("tabindex"));
    }

    [Fact]
    public async Task 鼠标移上去就高亮那一行_点和确认落在同一行()
    {
        _references.Items = ["a.cs", "b.cs"];
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(1);

        var cut = RenderComposer();
        cut.Find("textarea").Input("@");

        // 注意：bUnit 1.40 的 RefreshableElementCollection 索引器与 AngleSharp 1.8.5 不兼容，用 ElementAt。
        await cut.FindAll(".composer-menu-row").ElementAt(1).TriggerEventAsync("onmouseenter", new MouseEventArgs());

        var rows = cut.FindAll(".composer-menu-row").ToArray();
        Assert.False(rows[0].ClassList.Contains("is-active"));
        Assert.True(rows[1].ClassList.Contains("is-active"));
    }

    [Fact]
    public void 没有候选时确认只复位_不发送()
    {
        var sent = 0;
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(0);

        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "composer-test")
            .Add(component => component.WorkspaceRoot, _temp.Root)
            .Add(component => component.Text, "hello")
            .Add(component => component.OnSend, (ComposerSubmission _) => sent++));

        cut.InvokeAsync(() => cut.Instance.MenuCommitAsync());

        // JS 的菜单状态比组件晚一拍时，Enter 不能变成「发送一条空消息」或抛异常。
        Assert.Equal(0, sent);
        Assert.Equal("hello", cut.Instance.Text);
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





