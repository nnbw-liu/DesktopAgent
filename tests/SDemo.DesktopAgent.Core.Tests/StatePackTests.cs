using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 工作区事实的数据源与 P2a 的段渲染：扫描排除、缓存、最近改动与动作、
/// required 段不被预算剪掉、可选段可整段关闭。
/// </summary>
public sealed class StatePackTests : IDisposable
{
    private readonly HeuristicTokenCounter _counter = new();
    private readonly string _root;
    private readonly FrozenClock _clock = new(new DateTimeOffset(
        new DateTime(2026, 10, 24, 23, 0, 0),
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 24, 23, 0, 0))));

    public StatePackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-statepack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void 扫描排除构建目录并限制深度()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Api"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "root");
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "src");
        File.WriteAllText(Path.Combine(_root, "src", "Api", "deep.cs"), "deep");
        File.WriteAllText(Path.Combine(_root, "bin", "hidden.dll"), "x");

        var tree = new FileSystemWorkspaceTreeScanner().Scan(_root, 2, 120);

        Assert.Contains("src/", tree);
        Assert.Contains("src/a.cs", tree);
        Assert.Contains("src/Api/", tree);
        Assert.Contains("README.md", tree);
        Assert.DoesNotContain(tree, path => path.Contains("deep.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, path => path.StartsWith("bin/", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, path => path.StartsWith("obj/", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, path => path.StartsWith("node_modules/", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, path => path.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Fact]
    public void 扫描条目数有上限()
    {
        for (var index = 0; index < 200; index++)
        {
            File.WriteAllText(Path.Combine(_root, $"file{index:D3}.txt"), "x");
        }

        var tree = new FileSystemWorkspaceTreeScanner().Scan(_root, 2, 120);

        Assert.Equal(120, tree.Count);
    }

    [Fact]
    public void 工作区段超上限时从尾部截断且保留根目录与结构提示()
    {
        var state = State(
            tree: Enumerable.Range(0, 200).Select(index => $"src/dir{index}/").ToList(),
            verification: "dotnet build → ok(0)");

        var render = Render(state, totalCap: 300);

        Assert.True(render.Tokens <= 300 + 40);
        var workspace = render.Sections.Single(section => section.Name == "workspace");
        Assert.True(workspace.Truncated);
        Assert.Contains("根目录", render.Text);
        Assert.Contains("结构是快照", render.Text);
        Assert.Contains("本段已截断", render.Text);
        // 验证段是 required：被挤到上限之外也必须在。
        Assert.Contains("dotnet build", render.Text);
    }

    [Fact]
    public void 最近改动与动作使用相对时间()
    {
        var state = new WorkspaceState(
            Root: @"C:\Code\Demo",
            Tree: [],
            RecentEdits: [new RecentEdit("src/a.cs", "3f9a", _clock.Now.AddMinutes(-3).DateTime, "write_file")],
            RecentActions: [new RecentAction(_clock.Now.AddMinutes(-8).DateTime, "[工具] run_command dotnet build → ok")],
            LastVerification: "dotnet build → ok(0)",
            PinnedCount: 0);

        var render = Render(state, totalCap: 800);

        Assert.Contains("src/a.cs（sha256:3f9a，3 分钟前）", render.Text);
        Assert.Contains("[8 分钟前] [工具] run_command dotnet build → ok", render.Text);
    }

    [Fact]
    public void 关闭可选段后这些段严格不出现在快照里()
    {
        var settings = new ContextSettings { StatePackEnabled = false, PlanContextEnabled = false, TimeContextEnabled = false };
        var state = State(todos: [new TodoItem("写测试", TodoStatus.Pending)]);

        var render = Render(state, totalCap: 800, settings: settings);

        Assert.DoesNotContain("## 工作区", render.Text);
        Assert.DoesNotContain("最近动作", render.Text);
        Assert.DoesNotContain("当前计划", render.Text);
        Assert.DoesNotContain("当前时间", render.Text);
        Assert.DoesNotContain(render.Sections, section => section.Name is "workspace" or "actions" or "plan" or "time");
        // required 段不受开关影响。
        Assert.Contains(render.Sections, section => section.Name == "policy");
        Assert.Contains(render.Sections, section => section.Name == "verification");
    }

    [Fact]
    public void 关掉工作区段时不扫盘但仍保留验证()
    {
        var provider = new WorkspaceStateProvider(
            new CountingScanner(),
            _clock,
            treeTtl: TimeSpan.FromMinutes(1));
        var session = new ChatSession { Id = "s", Title = "t" };

        var state = provider.Build(_root, session, includeTree: false);

        Assert.Empty(state.Tree);
        Assert.Equal(0, provider.TreeScans);
    }

    [Fact]
    public void 工作区树30秒内缓存()
    {
        var scanner = new CountingScanner();
        var provider = new WorkspaceStateProvider(scanner, _clock);

        provider.Build(_root, null);
        provider.Build(_root, null);

        Assert.Equal(1, scanner.Calls);

        _clock.Advance(TimeSpan.FromSeconds(31));
        provider.Build(_root, null);

        Assert.Equal(2, scanner.Calls);
    }

    [Fact]
    public void 最近改动按最新去重且最多八条()
    {
        var session = new ChatSession { Id = "s", Title = "t" };
        var assistant = new ChatMessage(ChatMessageRole.Assistant, "改了很多");

        for (var index = 0; index < 12; index++)
        {
            assistant.ToolTrace.Add(new ToolTraceEntry(
                "write_file", ToolTraceEntry.StatusOk, null, $"f{index}.cs", "ok", 10,
                DateTime.Now.AddMinutes(index)));
        }

        // 重复写同一个文件：只保留最后一次。
        assistant.ToolTrace.Add(new ToolTraceEntry(
            "edit_file", ToolTraceEntry.StatusOk, null, "f0.cs", "ok", 10, DateTime.Now.AddMinutes(20)));
        session.Messages.Add(assistant);

        var provider = new WorkspaceStateProvider(new FileSystemWorkspaceTreeScanner(), _clock);
        var state = provider.Build(_root, session);

        Assert.Equal(8, state.RecentEdits.Count);
        Assert.Equal("f0.cs", state.RecentEdits[0].Path);
        Assert.Equal(8, state.RecentEdits.Select(edit => edit.Path).Distinct().Count());
    }

    [Fact]
    public void 计划段不超过二百token并留下截断标记()
    {
        var session = new ChatSession { Id = "s", Title = "t" };
        session.Todos = Enumerable.Range(1, 20)
            .Select(index => new TodoItem($"很长的计划项 {index} " + new string('x', 80), TodoStatus.Pending))
            .ToList();

        var state = new WorkspaceStateProvider().Build(string.Empty, session);
        var render = Render(state, totalCap: 1200);

        Assert.Contains("当前计划", render.Text);
        Assert.Contains("计划已截断", render.Text);
        Assert.True(render.TokensOf("plan") <= PlanContextSection.PlanTokenLimit + 24);
    }

    [Fact]
    public void 重名段注册直接抛异常()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ContextSectionRegistry([new PolicyContextSection(), new PolicyContextSection()]));

        Assert.Contains("上下文段重名", error.Message);
    }

    [Fact]
    public void 段顺序按中央常量表而不是注册顺序()
    {
        var registry = new ContextSectionRegistry(
        [
            new TimeContextSection(),
            new WorkspaceContextSection(),
            new PolicyContextSection()
        ]);

        Assert.Equal(
            ["workspace", "policy", "time"],
            registry.Sections.Select(section => section.Name).ToArray());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private WorkspaceState State(IReadOnlyList<string>? tree = null, string? verification = null, IReadOnlyList<TodoItem>? todos = null) =>
        new(
            Root: @"C:\Code\Demo",
            Tree: tree ?? ["src/", "README.md"],
            RecentEdits: [],
            RecentActions: [],
            LastVerification: verification,
            PinnedCount: 0,
            Todos: todos);

    private ContextSectionsRender Render(WorkspaceState state, int totalCap, ContextSettings? settings = null)
    {
        var input = new ContextSectionInput(
            _counter,
            settings ?? new ContextSettings(),
            AgentMode.Full,
            ModelProfilePresets.FromPreset("32K") ?? ModelProfile.Unknown,
            state,
            AgentPolicyFacts.Default,
            _clock.Now,
            "Asia/Shanghai",
            _clock.Now.AddMinutes(-12));

        return ContextSectionRegistry.Default.Render(input, totalCap);
    }

    private sealed class CountingScanner : IWorkspaceTreeScanner
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> Scan(string root, int depth, int maxEntries)
        {
            Calls++;
            return ["src/", "README.md"];
        }
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan delta) => Now += delta;
    }
}
