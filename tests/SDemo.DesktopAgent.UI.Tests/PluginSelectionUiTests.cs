using System.IO;
using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Components.Plugins;
using SDemo.DesktopAgent.Components.Settings;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>划词菜单、派生卡片「加入上下文」与设置页的划词开关。</summary>
public sealed class PluginSelectionUiTests : TestContext
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void 派生卡片可以加入上下文()
    {
        var artifact = Artifact();
        var toggled = 0;

        var cut = RenderComponent<DerivedCard>(parameters => parameters
            .Add(component => component.Artifact, artifact)
            .Add(component => component.PluginName, "简单问答")
            .Add(component => component.OnToggleContext, () => toggled++));

        Assert.Contains("加入上下文", cut.Markup);
        Assert.DoesNotContain("已加入上下文", cut.Markup);

        cut.FindAll(".derived-action")
            .First(button => button.TextContent.Contains("加入上下文"))
            .Click();

        Assert.Equal(1, toggled);
    }

    [Fact]
    public void 已加入上下文的卡片显示移出按钮与标记()
    {
        var cut = RenderComponent<DerivedCard>(parameters => parameters
            .Add(component => component.Artifact, Artifact(includeInContext: true))
            .Add(component => component.PluginName, "简单问答"));

        Assert.Contains("移出上下文", cut.Markup);
        Assert.Contains("已加入上下文", cut.Markup);
    }

    [Fact]
    public void 未完成的卡片不能加入上下文()
    {
        var artifact = Artifact() with { State = DerivedState.Running };

        var cut = RenderComponent<DerivedCard>(parameters => parameters
            .Add(component => component.Artifact, artifact)
            .Add(component => component.PluginName, "简单问答"));

        Assert.DoesNotContain("加入上下文", cut.Markup);
    }

    [Fact]
    public void 划词菜单渲染插件并可直接执行()
    {
        var first = Entry("quick-ask", "简单问答");
        var second = Entry("summarize", "总结");
        PluginActionEntry? ran = null;

        var cut = RenderComponent<SelectionActionBar>(parameters => parameters
            .Add(component => component.Entries, new[] { first, second })
            .Add(component => component.MaxVisible, 4)
            .Add(component => component.TextLength, 3)
            .Add(component => component.OnRun, entry => ran = entry));

        Assert.Contains("已选 3 字", cut.Markup);
        Assert.Contains("简单问答", cut.Markup);
        Assert.Contains("总结", cut.Markup);

        cut.FindAll(".plugin-action-button").First().Click();

        Assert.Equal(first, ran);
    }

    [Fact]
    public void 划词菜单超过上限时收进更多()
    {
        var entries = new[] { Entry("a", "甲"), Entry("b", "乙"), Entry("c", "丙") };

        var cut = RenderComponent<SelectionActionBar>(parameters => parameters
            .Add(component => component.Entries, entries)
            .Add(component => component.MaxVisible, 2));

        Assert.Equal(2, cut.FindAll(".plugin-action-button").Count);
        Assert.Contains("更多 1", cut.Markup);

        cut.Find(".selection-bar-more").Click();

        Assert.Equal(3, cut.FindAll(".plugin-action-button").Count);
    }

    [Fact]
    public void 聊天消息带划词锚点()
    {
        var settings = new InMemorySettings();
        ChatRoomFixture.SeedProvider(settings.Current);
        ChatRoomFixture.Register(this, settings, new StubSessionRunner(), _temp.Root);

        var message = new ChatMessage(ChatMessageRole.Assistant, "可以选中的正文");
        var session = new ChatSession
        {
            Id = "selection-anchor",
            Title = "划词",
            Messages = [message]
        };

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var article = cut.Find("article.msg");
        Assert.Equal(message.Id, article.GetAttribute("data-message-id"));
    }

    [Fact]
    public void 设置页有选中入口与划词上限()
    {
        var settings = WriteSelectionPlugin();
        RegisterPluginSettings(settings);

        var cut = RenderComponent<PluginSettings>();

        Assert.Contains("划词菜单最多显示", cut.Markup);
        Assert.Contains("选中", cut.Markup);
        Assert.Contains("演示", cut.Markup);
    }

    [Fact]
    public void 关闭选中开关会写入设置()
    {
        var settings = WriteSelectionPlugin();
        RegisterPluginSettings(settings);

        var cut = RenderComponent<PluginSettings>();

        var label = cut.FindAll(".plugin-control")
            .First(item => item.TextContent.Contains("选中"));
        label.QuerySelector("input")!.Change(false);

        Assert.False(settings.Current.Plugins.FindEntry("demo")!.InSelection);
    }

    private InMemorySettings WriteSelectionPlugin()
    {
        var settings = new InMemorySettings();
        var paths = new StubAppPaths(_temp.Root);
        var pluginRoot = Path.Combine(paths.PluginsRoot, "demo");
        Directory.CreateDirectory(Path.Combine(pluginRoot, "skills"));

        File.WriteAllText(
            Path.Combine(pluginRoot, "plugin.json"),
            """
            {
              "id": "demo",
              "name": "演示",
              "capabilities": [ { "id": "main", "name": "演示", "skill": "skills/main.md" } ],
              "mounts": [
                { "id": "selection", "surface": "selection", "capability": "main" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(pluginRoot, "skills", "main.md"),
            """
            ---
            name: 演示
            version: 1.0
            ---

            按规则处理。
            """);

        return settings;
    }

    private PluginCatalog RegisterPluginSettings(InMemorySettings settings)
    {
        var paths = new StubAppPaths(_temp.Root);
        var catalog = new PluginCatalog([], paths.PluginsRoot);
        catalog.Scan();
        var registry = new PluginRegistry(catalog, settings);

        Services.AddSingleton<ISettingsService>(settings);
        Services.AddSingleton(catalog);
        Services.AddSingleton(registry);

        return catalog;
    }

    private static PluginActionEntry Entry(string id, string name)
    {
        var capability = new PluginCapability
        {
            Id = "main",
            Name = name,
            SkillPath = "skills/main.md",
            SkillContent = "按规则处理。",
            Parameters = []
        };

        var mount = new PluginMount
        {
            PluginId = id,
            Id = "selection",
            Surface = PluginSurface.Selection,
            CapabilityId = "main",
            Capability = capability
        };

        var plugin = new PluginDescriptor
        {
            Id = id,
            Name = name,
            Source = PluginSource.Builtin,
            RootPath = Path.GetTempPath(),
            Capabilities = [capability],
            Mounts = [mount]
        };

        return new PluginActionEntry(plugin, mount, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>只为了让 ChatRoom 能渲染；不会真的跑对话。</summary>
    private sealed class StubSessionRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) => throw new NotSupportedException();

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }

    private static DerivedArtifact Artifact(bool includeInContext = false) => new(
        "a1",
        "quick-ask",
        "bubble",
        "quick-ask",
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        "简洁的回答",
        DerivedState.Done,
        null,
        "mock-model",
        100,
        10,
        5,
        DateTime.Now,
        IncludeInContext: includeInContext);
}
