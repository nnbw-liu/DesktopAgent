using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>todo_write：整体替换、上限、多个 in_progress、持久化、状态包预算。</summary>
public sealed class TodoTests
{
    [Fact]
    public void 整体替换而不是追加()
    {
        var session = ChatSession.CreateEmpty();
        session.Todos = [new TodoItem("旧项", TodoStatus.Completed)];

        var result = TodoToolPack.Write(session,
        [
            new TodoDraft { Content = "新项 1", Status = "completed" },
            new TodoDraft { Content = "新项 2", Status = "in_progress" }
        ]);

        Assert.Contains("已更新计划：1/2", result);
        Assert.Equal(["新项 1", "新项 2"], session.Todos.Select(item => item.Content));
    }

    [Fact]
    public void 超过二十项拒绝()
    {
        var session = ChatSession.CreateEmpty();
        var items = Enumerable.Range(1, 21)
            .Select(index => new TodoDraft { Content = $"步骤 {index}" })
            .ToArray();

        var result = TodoToolPack.Write(session, items);

        Assert.Contains("最多 20 项", result);
        Assert.Empty(session.Todos);
    }

    [Fact]
    public void 内容过长拒绝()
    {
        var session = ChatSession.CreateEmpty();

        var result = TodoToolPack.Write(session,
            [new TodoDraft { Content = new string('x', TodoToolPack.MaxContentChars + 1) }]);

        Assert.Contains("200", result);
        Assert.Empty(session.Todos);
    }

    [Fact]
    public void 多个进行中只保留第一个()
    {
        var session = ChatSession.CreateEmpty();

        var result = TodoToolPack.Write(session,
        [
            new TodoDraft { Content = "A", Status = "in_progress" },
            new TodoDraft { Content = "B", Status = "in_progress" }
        ]);

        Assert.Contains("只保留第一个", result);
        Assert.Equal(TodoStatus.InProgress, session.Todos[0].Status);
        Assert.Equal(TodoStatus.Pending, session.Todos[1].Status);
    }

    [Fact]
    public async Task 持久化往返()
    {
        using var temp = new TempDirectory();
        var store = new ChatStore(Path.Combine(temp.Root, "chats"));
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "hi"));
        session.Todos =
        [
            new TodoItem("改代码", TodoStatus.Completed),
            new TodoItem("跑测试", TodoStatus.InProgress)
        ];

        await store.SaveSessionAsync(session);
        var loaded = Assert.Single(await store.LoadAllAsync([]));

        Assert.Equal(2, loaded.Todos.Count);
        Assert.Equal(TodoStatus.InProgress, loaded.Todos[1].Status);
        Assert.Equal("跑测试", loaded.Todos[1].Content);
    }

    [Fact]
    public async Task 工具函数可以接收计划数组()
    {
        var session = ChatSession.CreateEmpty();
        var context = new ToolContext(
            "s1",
            Path.GetTempPath(),
            [],
            new StubSettingsService(),
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            Session: session);
        var function = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            new TodoToolPack().Create(context).Single(item => item.Name == TodoToolPack.FunctionId));

        var result = await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["items"] = new[]
                {
                    new TodoDraft { Content = "步骤一", Status = "in_progress" },
                    new TodoDraft { Content = "步骤二", Status = "pending" }
                }
            }));

        Assert.Contains("0/2", result?.ToString());
        Assert.Equal(TodoStatus.InProgress, session.Todos[0].Status);
        Assert.Equal(2, session.Todos.Count);
    }

    [Fact]
    public void 状态包计划不超过二百token()
    {
        var session = ChatSession.CreateEmpty();
        session.Todos = Enumerable.Range(1, 20)
            .Select(index => new TodoItem($"很长的计划项 {index} " + new string('x', 80), TodoStatus.Pending))
            .ToList();
        var state = new WorkspaceStateProvider().Build(string.Empty, session);

        var rendered = StatePackFormatter.Format(state, 1200, new HeuristicTokenCounter());

        Assert.Contains("## 当前计划", rendered.Text);
        Assert.Contains("计划已截断", rendered.Text);
    }
}
