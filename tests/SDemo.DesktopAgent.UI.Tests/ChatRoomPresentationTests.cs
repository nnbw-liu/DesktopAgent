using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 对标 DSH 的对话呈现：
/// 1) 思维链默认收成一行摘要，点开才看全文；
/// 2) 宿主自动追加的提示不再伪装成用户气泡，续答并进同一个回合。
/// </summary>
public sealed class ChatRoomPresentationTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();

    public ChatRoomPresentationTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, new QuietRunner(), _temp.Root);
    }

    [Fact]
    public void 思维链默认折叠成一行并显示字数()
    {
        var session = new ChatSession { Id = "reasoning", Title = "推理" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "解释一下为什么"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, "因为所以。")
        {
            Reasoning = "先看调用链。\n再看缓存命中。\n最后是取整方式。"
        };

        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var summary = cut.Find(".reasoning-summary").TextContent;

        Assert.Contains("已思考", summary, StringComparison.Ordinal);
        Assert.Contains("字", summary, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".reasoning-body"));

        // 折叠时贴出最后一行，用户不用点开也能知道模型在想什么。
        Assert.Contains("最后是取整方式", cut.Find(".reasoning-preview").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 点一下才展开完整思维链()
    {
        var session = new ChatSession { Id = "reasoning-open", Title = "推理" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "解释一下为什么"));
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "因为所以。")
        {
            Reasoning = "第一行\n第二行"
        });

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find(".reasoning-toggle").Click();

        var body = cut.Find(".reasoning-body");
        Assert.Contains("第一行", body.TextContent, StringComparison.Ordinal);
        Assert.Contains("第二行", body.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 宿主追问压成一行且续答并进同一块()
    {        var session = new ChatSession { Id = "grouped", Title = "合并" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "帮我改 file.cs"));
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "我先看一下。"));

        var prompt = new ChatMessageModel(
            ChatMessageRole.User,
            "[宿主提示] 本轮没有检测到任何文件修改，任务尚未完成。请先定位文件。");

        session.Messages.Add(prompt);
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "已经改好了。"));

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        // 用户手打的只有一条；宿主提示单独成行，不进用户气泡。
        Assert.Single(cut.FindAll(".msg--user"));
        var hostPrompt = cut.Find(".host-prompt");
        Assert.Contains("宿主追问", hostPrompt.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[宿主提示]", hostPrompt.TextContent, StringComparison.Ordinal);

        // 两次助手输出：第二条是续答，不再重复角色名。
        Assert.Equal(2, cut.FindAll(".msg--assistant").Count);
        Assert.Single(cut.FindAll(".msg--cont"));
        Assert.Contains("帮我改 file.cs", cut.Find(".msg--user").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 助手消息里能看到调用了哪些工具()
    {
        var session = new ChatSession { Id = "tools", Title = "工具" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看一下 README 并跑一下测试"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, "看完了，测试也过了。");
        reply.ToolTrace.Add(new ToolTraceEntry(
            "read_file", ToolTraceEntry.StatusOk, null, "README.md", "读取 120 行", 3200, DateTime.Now));
        reply.ToolTrace.Add(new ToolTraceEntry(
            "run_powershell", ToolTraceEntry.StatusError, 1, "dotnet test", "退出码 1", 800, DateTime.Now));
        reply.ToolCalls["read_file"] = 1;
        reply.ToolCalls["run_powershell"] = 1;
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var panel = cut.Find(".tool-trace");
        Assert.Contains("工具调用 2 次", panel.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll(".tool-trace-row").Count);
        Assert.Single(cut.FindAll(".tool-trace-row.is-error"));
        Assert.Contains("read_file", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("run_powershell", panel.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 步骤时间线按思考与工具交替渲染()
    {
        var session = new ChatSession { Id = "timeline", Title = "时间线" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看看 README"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, "我已经看完了。");
        reply.Reasoning = "先看目录。再看文件。";
        reply.ToolTrace.Add(new ToolTraceEntry(
            "read_file", ToolTraceEntry.StatusOk, null, "README.md", "读取 120 行", 3200, DateTime.Now, null, 5, 0));
        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "先看目录。"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, 0));
        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "再看文件。"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Text, "我已经看完了。"));
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var stepClasses = cut.FindAll(".timeline-step")
            .Select(element => element.ClassList.Contains("timeline-step--tool") ? "tool"
                : element.ClassList.Contains("timeline-step--reasoning") ? "reasoning"
                : "text")
            .ToArray();

        Assert.Equal(["reasoning", "tool", "reasoning", "text"], stepClasses);
        Assert.Equal(2, cut.FindAll(".reasoning-toggle").Count);
        Assert.Contains("read_file", cut.Find(".timeline-step--tool").TextContent, StringComparison.Ordinal);
        Assert.Contains("我已经看完了。", cut.Find(".timeline-step--text").TextContent, StringComparison.Ordinal);

        // 正文只在时间线里出现一次，旧的分组块不再重复渲染。
        Assert.Empty(cut.FindAll(".tool-trace"));
        Assert.Single(cut.FindAll(".msg-body--markdown"));
    }

    /// <summary>只渲染不生成：预览直接失败、流式没有内容。</summary>
    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("呈现测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("呈现测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("呈现测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
