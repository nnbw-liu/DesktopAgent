using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 输入框里的聊天室级权限：完全权限（跳过确认 + 关沙箱）与沙箱开关贴在工具 chip 前面，
/// 技能设置里关掉的技能不再占固定 chip，但仍能从 <c>/</c> 菜单调用。
/// </summary>
public sealed class ChatComposerAccessTests : TestContext
{
    private readonly TempDirectory _temp = new();

    private static readonly SkillInfo[] Skills =
    [
        new("translate", "把文本翻成目标语言"),
        new("archify", "画架构图")
    ];

    public ChatComposerAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new AttachmentStore(_temp.Root));
        Services.AddSingleton<IComposerReferenceIndex>(new EmptyReferences());
    }

    [Fact]
    public void 完全权限与沙箱按钮排在工具chip之前并回调()
    {
        bool? fullAccess = null;
        var sandboxToggles = 0;

        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "chat-1")
            .Add(component => component.Skills, Skills)
            .Add(component => component.WorkspaceRoot, _temp.Root)
            .Add(component => component.Text, string.Empty)
            .Add(component => component.SandboxEnabled, true)
            .Add(component => component.FullAccess, false)
            .Add(component => component.OnToggleFullAccess, (bool enabled) => fullAccess = enabled)
            .Add(component => component.OnToggleSandbox, () => sandboxToggles++));

        var accessButtons = cut.FindAll(".composer-actions button.composer-tool").ToArray();

        // 两个控件都在工具 chip 之前：完全权限、沙箱，然后是搜索这类工具 chip。
        Assert.Equal("完全权限", accessButtons[0].GetAttribute("aria-label"));
        Assert.Equal("沙箱", accessButtons[1].GetAttribute("aria-label"));

        // 每次点击前重新查：回调触发重渲染后，旧元素上的事件 ID 已经失效。
        cut.InvokeAsync(() => cut.FindAll(".composer-actions button.composer-tool").ToArray()[0].Click());
        Assert.True(fullAccess);

        cut.InvokeAsync(() => cut.FindAll(".composer-actions button.composer-tool").ToArray()[1].Click());
        Assert.Equal(1, sandboxToggles);
    }

    [Fact]
    public void 沙箱关闭时按钮显示为关闭态()
    {
        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "chat-1")
            .Add(component => component.SandboxEnabled, false));

        var sandbox = cut.FindAll(".composer-actions button.composer-tool").ToArray()[1];

        Assert.False(sandbox.HasAttribute("disabled"));
        Assert.Contains("is-off", sandbox.ClassList);
        Assert.Contains("沙箱：已关闭", sandbox.GetAttribute("title"));
    }

    [Fact]
    public void 沙箱不可用时不锁死开关而是在提示里说明()
    {
        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "chat-1")
            .Add(component => component.SandboxEnabled, false)
            .Add(component => component.SandboxUnavailableReason, "Windows Sandbox 未安装"));

        var sandbox = cut.FindAll(".composer-actions button.composer-tool").ToArray()[1];

        // 开关永远可用：重新打开是用户的决定，沙箱不可用只影响执行时的结果。
        Assert.False(sandbox.HasAttribute("disabled"));
        Assert.Contains("Windows Sandbox 未安装", sandbox.GetAttribute("title"));
        Assert.Contains("仍会因沙箱不可用而拒绝执行", sandbox.GetAttribute("title"));
    }

    [Fact]
    public void 没有聊天室时不渲染权限控件()
    {
        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, string.Empty)
            .Add(component => component.Skills, Skills));

        Assert.Empty(cut.FindAll(".composer-actions button.composer-tool"));
    }

    [Fact]
    public void 技能设置里关掉的技能不再占chip但仍能从斜杠菜单调用()
    {
        JSInterop.Setup<int>("sdComposer.caretOf", invocation => true).SetResult(1);

        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.SessionId, "chat-1")
            .Add(component => component.Skills, Skills)
            .Add(component => component.HiddenSkills, new HashSet<string>(["archify"])));

        // chip 里只剩 translate，archify 不再固定占位置。
        var chips = cut.FindAll(".skill-list .skill-chip").Select(element => element.TextContent).ToArray();
        Assert.Contains(chips, text => text.Contains("translate"));
        Assert.DoesNotContain(chips, text => text.Contains("archify"));

        // 但 `/` 菜单仍然能找到它：隐藏只影响输入框的展示。
        cut.Find("textarea").Input("/arc");

        Assert.Contains(
            cut.FindAll(".composer-menu-label"),
            label => label.TextContent.Contains("/archify"));
    }

    private sealed class EmptyReferences : IComposerReferenceIndex
    {
        public IReadOnlyList<string> List(string? rootPath) => [];

        public void Invalidate()
        {
        }
    }
}
