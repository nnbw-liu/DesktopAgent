using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P7 产品化：没有成功编辑就宣布完成时，宿主最多回灌 3 次。</summary>
public sealed class NoEditRepromptTests
{
    [Fact]
    public void 任务型输入读文件但没编辑时回灌()
    {
        var metrics = new AgentRunMetrics();
        metrics.RecordToolTrace(Entry("read_file", ToolTraceEntry.StatusOk));

        var decision = NoEditRepromptPolicy.Decide(
            metrics,
            "给设置页加一个开关",
            "完成：已经加好了。",
            alreadyAttempted: 0,
            compact: false);

        Assert.True(decision.Continue);
        Assert.Equal(1, decision.Attempt);
        Assert.Contains("没有检测到任何文件修改", decision.Prompt);
    }

    [Fact]
    public void 有成功编辑时不再回灌()
    {
        var metrics = new AgentRunMetrics();
        metrics.RecordToolTrace(Entry(
            "apply_patch",
            ToolTraceEntry.StatusOk,
            [new EditedFile("src/App.cs", 2, 1)]));

        var decision = NoEditRepromptPolicy.Decide(
            metrics,
            "修一下这个 bug",
            "完成：已修复。",
            alreadyAttempted: 0,
            compact: false);

        Assert.False(decision.Continue);
    }

    [Fact]
    public void 被拒绝的编辑不算成功()
    {
        var metrics = new AgentRunMetrics();
        metrics.RecordToolTrace(Entry("apply_patch", ToolTraceEntry.StatusRejected));

        var decision = NoEditRepromptPolicy.Decide(
            metrics,
            "修一下这个 bug",
            "完成：已修复。",
            alreadyAttempted: 0,
            compact: false);

        Assert.True(decision.Continue);
    }

    [Fact]
    public void Compact模式不打扰()
    {
        var metrics = new AgentRunMetrics();
        metrics.RecordToolTrace(Entry("read_file", ToolTraceEntry.StatusOk));

        var decision = NoEditRepromptPolicy.Decide(
            metrics,
            "给设置页加一个开关",
            "完成：已经加好了。",
            alreadyAttempted: 0,
            compact: true);

        Assert.False(decision.Continue);
    }

    [Fact]
    public void 反问和纯聊天不回灌()
    {
        var metrics = new AgentRunMetrics();
        metrics.RecordToolTrace(Entry("read_file", ToolTraceEntry.StatusOk));

        var question = NoEditRepromptPolicy.Decide(
            metrics,
            "给设置页加一个开关",
            "你想加在哪个分区？",
            alreadyAttempted: 0,
            compact: false);

        Assert.False(question.Continue);

        var chat = NoEditRepromptPolicy.Decide(
            new AgentRunMetrics(),
            "你好",
            "你好，有什么可以帮你？",
            alreadyAttempted: 0,
            compact: false);

        Assert.False(chat.Continue);

        var explanation = NoEditRepromptPolicy.Decide(
            metrics,
            "这个文件是干什么的",
            "它负责解析命令行参数。",
            alreadyAttempted: 0,
            compact: false);

        Assert.False(explanation.Continue);
    }

    [Fact]
    public void 最多三次()
    {
        var metrics = new AgentRunMetrics();
        metrics.RecordToolTrace(Entry("read_file", ToolTraceEntry.StatusOk));

        var decision = NoEditRepromptPolicy.Decide(
            metrics,
            "给设置页加一个开关",
            "完成：已经加好了。",
            alreadyAttempted: 3,
            compact: false);

        Assert.False(decision.Continue);
    }

    private static ToolTraceEntry Entry(
        string tool,
        string status,
        IReadOnlyList<EditedFile>? edits = null) =>
        new(tool, status, null, null, "summary", 10, DateTime.Now, edits);
}
