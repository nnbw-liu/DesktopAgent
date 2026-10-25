using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 重开会话时的步骤还原：工具轨迹里记着「调用那一刻已经产生了多少字」，
/// 用它把思维链与正文切开，重新排成 思考 → 工具 → 思考 → … → 正文。
/// </summary>
public sealed class ChatTimelineTests
{
    [Fact]
    public void 按字符边界还原成交错步骤()
    {
        const string reasoning = "先看目录。再看文件。最后改一行。";
        const string content = "我看一下。已经改好了。";

        var trace = new[]
        {
            Entry("glob_files", 5, 0),
            Entry("read_file", 10, 5),
            Entry("apply_patch", 16, 5)
        };

        var steps = ChatTimelineBuilder.FromTrace(reasoning, content, trace);

        Assert.Equal(
            [
                ChatStepKind.Reasoning,
                ChatStepKind.Tool,
                ChatStepKind.Reasoning,
                ChatStepKind.Preamble,
                ChatStepKind.Tool,
                ChatStepKind.Reasoning,
                ChatStepKind.Tool,
                ChatStepKind.Text
            ],
            steps.Select(step => step.Kind));

        Assert.Equal("先看目录。", steps[0].Text);
        Assert.Equal("再看文件。", steps[2].Text);
        Assert.Equal("我看一下。", steps[3].Text);
        Assert.Equal(0, steps[1].ToolIndex);
        Assert.Equal(1, steps[4].ToolIndex);
        Assert.Equal("最后改一行。", steps[5].Text);
        Assert.Equal(2, steps[6].ToolIndex);
        Assert.Equal("已经改好了。", steps[7].Text);

        // 工具调用前说的正文属于过程，收起过程组时一起隐藏；最后一段才是答案。
        Assert.Equal(ChatGroupPart.Process, steps[3].Part);
        Assert.Equal(ChatGroupPart.Answer, steps[7].Part);
    }

    [Fact]
    public void 旧记录没有边界时回退为空()
    {
        var trace = new[]
        {
            new ToolTraceEntry("read_file", ToolTraceEntry.StatusOk, null, null, null, 10, DateTime.Now)
        };

        Assert.Empty(ChatTimelineBuilder.FromTrace("想了想", "结果", trace));
    }

    [Fact]
    public void 还原的步骤带上调用id与分组()
    {
        var trace = new[]
        {
            new ToolTraceEntry("read_file", ToolTraceEntry.StatusOk, null, null, "读取", 10, DateTime.Now, null, 2, 1, "call-7")
        };

        var steps = ChatTimelineBuilder.FromTrace("先看。", "看完了。", trace);
        var tool = Assert.Single(steps, step => step.Kind == ChatStepKind.Tool);

        Assert.Equal("call-7", tool.CallId);
        Assert.Equal(ChatGroupPart.Process, tool.Part);
        Assert.All(
            steps.Where(step => step.Kind == ChatStepKind.Text),
            step => Assert.Equal(ChatGroupPart.Answer, step.Part));
    }

    private static ToolTraceEntry Entry(string tool, int reasoningChars, int textChars) =>
        new(
            tool,
            ToolTraceEntry.StatusOk,
            null,
            null,
            tool,
            10,
            DateTime.Now,
            null,
            reasoningChars,
            textChars);
}
