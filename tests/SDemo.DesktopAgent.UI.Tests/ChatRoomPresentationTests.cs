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
    public void 工具执行中立即显示运行中()
    {
        var session = new ChatSession { Id = "tool-running", Title = "运行中" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "跑一下测试"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, string.Empty);
        reply.ToolTrace.Add(new ToolTraceEntry(
            "run_powershell",
            ToolTraceEntry.StatusRunning,
            null,
            "dotnet test",
            null,
            0,
            DateTime.Now,
            ReasoningChars: 0,
            TextChars: 0));
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var toolStep = cut.Find(".timeline-step--tool");
        Assert.Contains("运行中", toolStep.TextContent, StringComparison.Ordinal);
        Assert.Contains("is-running", toolStep.ClassList);
    }

    [Fact]
    public void 步骤时间线按思考与工具交替渲染()
    {
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Expanded;

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

        // 完全展开档不分过程组。
        Assert.Empty(cut.FindAll(".process-head"));
    }

    [Fact]
    public void 标准档把过程收成一组但保留答案()
    {
        var session = new ChatSession { Id = "grouped-standard", Title = "过程组" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看看 README"));

        var reply = BuildTimelineReply();
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        // 过程行收起，组头给出步数与工具数。
        Assert.Empty(cut.FindAll(".timeline-step--tool"));
        var head = cut.Find(".process-head");
        Assert.Contains("过程", head.TextContent, StringComparison.Ordinal);
        Assert.Contains("3 步", cut.Find(".process-meta").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 次工具", cut.Find(".process-meta").TextContent, StringComparison.Ordinal);

        // 答案永远在组外：折叠不影响正文。
        Assert.Contains("我已经看完了。", cut.Find(".msg-body--markdown").TextContent, StringComparison.Ordinal);

        // 点一下组头就展开过程行。
        cut.Find(".process-head").Click();
        Assert.Single(cut.FindAll(".timeline-step--tool"));
        Assert.Equal(2, cut.FindAll(".reasoning-toggle").Count);
    }

    [Fact]
    public void 工具调用前的正文收进过程组只留最后一段答案()
    {
        var session = new ChatSession { Id = "preamble", Title = "过程正文" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看看 README"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, "我先读一下 README。\n\n读完了，结论在这里。")
        {
            Reasoning = "先看目录。"
        };

        reply.ToolTrace.Add(new ToolTraceEntry(
            "read_file", ToolTraceEntry.StatusOk, null, "README.md", "读取 120 行", 3200, DateTime.Now, null, 5, 8, "call-1"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "先看目录。"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Preamble, "我先读一下 README。"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, 0, 0, "call-1"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Text, "读完了，结论在这里。"));
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        // 收起时过程正文跟思维链、工具行一起隐藏，只留最终答案。
        Assert.Empty(cut.FindAll(".timeline-step--preamble"));
        Assert.Contains("读完了，结论在这里。", cut.Find(".msg-body--markdown").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("我先读一下", cut.Find(".msg-body--markdown").TextContent, StringComparison.Ordinal);

        // 展开过程组：过程正文出现在工具行之前，和真实顺序一致。
        cut.Find(".process-head").Click();

        var preamble = cut.Find(".timeline-step--preamble");
        Assert.Contains("我先读一下 README。", preamble.TextContent, StringComparison.Ordinal);
        Assert.Contains("3 步", cut.Find(".process-meta").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 详细档默认展开过程行()
    {
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Detailed;

        var session = new ChatSession { Id = "grouped-detailed", Title = "详细" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看看 README"));
        session.Messages.Add(BuildTimelineReply());

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Single(cut.FindAll(".timeline-step--tool"));
        Assert.Equal(2, cut.FindAll(".reasoning-toggle").Count);
        Assert.Single(cut.FindAll(".process-head"));
    }

    [Fact]
    public void 工具行点开显示参数与结果预览()
    {
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Expanded;

        var session = new ChatSession { Id = "tool-card", Title = "工具卡" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "跑一下测试"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, "跑完了。");
        reply.ToolTrace.Add(new ToolTraceEntry(
            "run_command",
            ToolTraceEntry.StatusError,
            1,
            "dotnet test",
            "退出码 1",
            800,
            DateTime.Now,
            ReasoningChars: 0,
            TextChars: 0,
            CallId: "call-1",
            ArgsJson: "{\"command\":\"dotnet\",\"args\":\"test\"}",
            Title: "dotnet test",
            DurationMs: 8400,
            ResultPreview: "Failed! - Failed: 1, Passed: 12"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, 0, 0, "call-1"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Text, "跑完了。"));
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var row = cut.Find(".timeline-tool-row");
        Assert.Contains("8.4 s", row.TextContent, StringComparison.Ordinal);

        row.Click();

        Assert.Contains("dotnet test", cut.Find(".timeline-tool-target").TextContent, StringComparison.Ordinal);
        Assert.Contains("\"args\"", cut.Find(".timeline-tool-args").TextContent, StringComparison.Ordinal);
        Assert.Contains("Passed: 12", cut.Find(".timeline-tool-result").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 切换档位保留手动展开的过程组()
    {
        var session = new ChatSession { Id = "grouped-switch", Title = "切档" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看看 README"));
        session.Messages.Add(BuildTimelineReply());

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find(".process-head").Click();
        Assert.Single(cut.FindAll(".timeline-step--tool"));

        // 切到详细档：过程行本来就可见。
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Detailed;
        _settings.Save();
        cut.WaitForAssertion(() => Assert.Equal(ProcessDisplayMode.Detailed, _settings.Current.Chat.ProcessMode));

        // 再切回标准档：用户手点过展开，自动收起不再覆盖他。
        _settings.Current.Chat.ProcessMode = ProcessDisplayMode.Standard;
        _settings.Save();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".timeline-step--tool")));
    }

    [Fact]
    public void 轨迹条目原地替换后工具行会刷新状态()
    {
        var session = new ChatSession { Id = "tool-live-update", Title = "实时状态" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "看看 README"));

        var reply = new ChatMessageModel(ChatMessageRole.Assistant, string.Empty);
        reply.ToolTrace.Add(new ToolTraceEntry(
            "read_file",
            ToolTraceEntry.StatusRunning,
            null,
            "README.md",
            null,
            0,
            DateTime.Now,
            ReasoningChars: 0,
            TextChars: 0,
            CallId: "call-live"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, 0, 0, "call-live"));
        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Contains("运行中", cut.Find(".timeline-step--tool").TextContent, StringComparison.Ordinal);

        // 结果回来：宿主按 CallId 原地替换轨迹条目（列表引用不变），然后重渲染。
        reply.ToolTrace[0] = reply.ToolTrace[0] with
        {
            Status = ToolTraceEntry.StatusOk,
            Summary = "读取 120 行",
            DurationMs = 120
        };

        cut.Render();

        // 工具跑完：这一轮不再「进行中」，过程按标准档收成组头（组头数字来自替换后的条目）。
        Assert.Empty(cut.FindAll(".timeline-step--tool"));
        Assert.Contains("1 次工具", cut.Find(".process-meta").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("失败", cut.Find(".process-meta").TextContent, StringComparison.Ordinal);

        // 展开过程组，确认行内的状态、摘要与耗时都换成了结果态。
        cut.Find(".process-head").Click();

        var row = cut.Find(".timeline-step--tool");
        Assert.Contains("成功", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("读取 120 行", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("120 ms", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 压缩中断时显示未完成标记()
    {
        var session = new ChatSession { Id = "compaction-interrupted", Title = "压缩中断" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "改一下 README"));
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.Assistant, "好的。"));

        // P2b：只有开始标记、没有收尾 —— 上一次进程崩在压缩中途（摘要没有提交）。
        session.CompactionStartedAt = new DateTime(2026, 10, 25, 12, 0, 0);
        session.CompactionReason = "auto";

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        var badge = cut.Find(".chat-summary-badge--warn");
        Assert.Contains("上次压缩未完成", badge.TextContent, StringComparison.Ordinal);
        Assert.Contains("但没有收尾", badge.GetAttribute("title"), StringComparison.Ordinal);

        // 摘要没提交：不出现分隔线、也不影响原文渲染。
        Assert.Empty(cut.FindAll(".compaction-divider"));
        Assert.Equal(2, cut.FindAll(".msg").Count);
    }

    private static ChatMessageModel BuildTimelineReply()
    {
        var reply = new ChatMessageModel(ChatMessageRole.Assistant, "我已经看完了。")
        {
            Reasoning = "先看目录。再看文件。",
            DurationMs = 12400
        };

        reply.ToolTrace.Add(new ToolTraceEntry(
            "read_file", ToolTraceEntry.StatusOk, null, "README.md", "读取 120 行", 3200, DateTime.Now, null, 5, 0, "call-1"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "先看目录。"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Tool, string.Empty, 0, 0, "call-1"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Reasoning, "再看文件。"));
        reply.Steps.Add(new ChatStep(ChatStepKind.Text, "我已经看完了。"));

        return reply;
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
