using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 工作记忆状态包：扫描排除、缓存、最近改动与预算截断顺序。</summary>
public sealed class StatePackTests : IDisposable
{
    private readonly HeuristicTokenCounter _counter = new();
    private readonly string _root;

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
    public void 状态包在预算内且保留验证块()
    {
        var state = new WorkspaceState(
            Root: @"C:\Code\Demo",
            Tree: Enumerable.Range(0, 200).Select(index => $"src/dir{index}/").ToList(),
            RecentEdits:
            [
                new RecentEdit("src/a.cs", "3f9a", DateTime.Now, "write_file"),
                new RecentEdit("src/b.cs", "8b21", DateTime.Now, "apply_patch")
            ],
            RecentActions:
            [
                new RecentAction(DateTime.Now, "[工具] read_file src/a.cs → ok"),
                new RecentAction(DateTime.Now, "[工具] run_command dotnet build → error(exit 1)")
            ],
            LastVerification: "dotnet build → error(exit 1)",
            PinnedCount: 2);

        var render = StatePackFormatter.Format(state, 300, _counter);

        Assert.True(render.Tokens <= 300);
        Assert.True(render.Truncated);
        Assert.Contains("## 验证", render.Text);
        Assert.Contains("dotnet build", render.Text);
        Assert.Contains("根目录", render.Text);
    }

    [Fact]
    public void 截断优先结构再改动最后动作()
    {
        var edit = new RecentEdit("src/keep.cs", "abcd", DateTime.Now, "write_file");
        var state = new WorkspaceState(
            Root: @"C:\Demo",
            Tree: Enumerable.Range(0, 120).Select(index => $"src/tree{index}/").ToList(),
            RecentEdits: [edit],
            RecentActions: [new RecentAction(DateTime.Now, "[工具] write_file src/keep.cs → ok")],
            LastVerification: null,
            PinnedCount: 0);

        var render = StatePackFormatter.Format(state, 160, _counter);

        Assert.True(render.Tokens <= 160);
        Assert.Contains("## 验证", render.Text);
        Assert.True(render.Truncated);
        Assert.True(render.TreeEntries < 120);
        Assert.Equal(1, render.Edits);
        Assert.Equal(1, render.Actions);
    }

    [Fact]
    public void 常规状态包不超过八百与小模型五百预算()
    {
        var state = new WorkspaceState(
            Root: @"C:\Code\Demo",
            Tree: Enumerable.Range(0, 30).Select(index => $"src/module{index}/").ToList(),
            RecentEdits: [new RecentEdit("src/a.cs", "3f9a", DateTime.Now, "write_file")],
            RecentActions: [new RecentAction(DateTime.Now, "[工具] write_file src/a.cs → ok")],
            LastVerification: "dotnet build → ok(0)",
            PinnedCount: 1);

        var normal = StatePackFormatter.Format(state, 800, _counter);
        var small = StatePackFormatter.Format(state, 500, _counter);

        Assert.True(normal.Tokens <= 800);
        Assert.True(small.Tokens <= 500);
        Assert.Contains("src/a.cs", normal.Text);
        Assert.Contains("dotnet build", normal.Text);
        Assert.Contains("## 最近动作", normal.Text);
    }

    [Fact]
    public void 状态包渲染最近改动与动作()
    {
        var session = new ChatSession { Id = "s", Title = "t" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "改文件"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "完成")
        {
            ToolTrace =
            [
                new ToolTraceEntry("write_file", ToolTraceEntry.StatusOk, null, "a.cs", "ok", 10, DateTime.Now),
                new ToolTraceEntry("read_file", ToolTraceEntry.StatusOk, null, "b.cs", "ok", 10, DateTime.Now)
            ]
        });

        File.WriteAllText(Path.Combine(_root, "a.cs"), "alpha");

        var provider = new WorkspaceStateProvider(
            new FileSystemWorkspaceTreeScanner(),
            new FakeTimeProvider(DateTimeOffset.Now));
        var state = provider.Build(_root, session);
        var render = StatePackFormatter.Format(state, 800, _counter);

        Assert.Single(state.RecentEdits);
        Assert.Equal("a.cs", state.RecentEdits[0].Path);
        Assert.False(string.IsNullOrWhiteSpace(state.RecentEdits[0].HashPrefix));
        Assert.Equal(2, state.RecentActions.Count);
        Assert.Contains("a.cs", render.Text);
    }

    [Fact]
    public void 工作区树30秒内缓存()
    {
        var scanner = new CountingScanner();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new WorkspaceStateProvider(scanner, time);

        provider.Build(_root, null);
        provider.Build(_root, null);

        Assert.Equal(1, scanner.Calls);

        time.Advance(TimeSpan.FromSeconds(31));
        provider.Build(_root, null);

        Assert.Equal(2, scanner.Calls);
    }

    [Fact]
    public void 最近改动按最新去重且最多八条()
    {
        var session = new ChatSession { Id = "s", Title = "t" };
        var assistant = new ChatMessage(ChatMessageRole.Assistant, "改了很多")
        {
            ToolTrace = []
        };

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

        var provider = new WorkspaceStateProvider(
            new FileSystemWorkspaceTreeScanner(),
            new FakeTimeProvider(DateTimeOffset.UtcNow));
        var state = provider.Build(_root, session);

        Assert.Equal(8, state.RecentEdits.Count);
        Assert.Equal("f0.cs", state.RecentEdits[0].Path);
        Assert.Equal(8, state.RecentEdits.Select(edit => edit.Path).Distinct().Count());
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

    private sealed class CountingScanner : IWorkspaceTreeScanner
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> Scan(string root, int depth, int maxEntries)
        {
            Calls++;
            return ["src/", "README.md"];
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
