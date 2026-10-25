using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P0 的过程展示：档位 → 策略、步骤分组、工具行数据。
/// 这些是纯数据/纯映射，先把契约钉死，UI 只在上面做渲染。
/// </summary>
public sealed class ProcessDisplayTests
{
    [Theory]
    [InlineData(ProcessDisplayMode.Compact, true, ProcessGrouping.Collapsed, false, false)]
    [InlineData(ProcessDisplayMode.Standard, true, ProcessGrouping.Collapsed, true, true)]
    [InlineData(ProcessDisplayMode.Detailed, true, ProcessGrouping.History, true, true)]
    [InlineData(ProcessDisplayMode.Expanded, false, ProcessGrouping.None, false, true)]
    public void 档位映射成四个开关(
        ProcessDisplayMode mode,
        bool foldCompleted,
        ProcessGrouping grouping,
        bool liveDetail,
        bool settledPreview)
    {
        var policy = ProcessDisplayPolicy.For(mode);

        Assert.Equal(foldCompleted, policy.FoldCompletedTurns);
        Assert.Equal(grouping, policy.Grouping);
        Assert.Equal(liveDetail, policy.LiveProcessDetail);
        Assert.Equal(settledPreview, policy.SettledReasoningPreview);
    }

    [Fact]
    public void 设置里的收起时机覆盖档位默认值()
    {
        var settings = new ChatPresentationSettings
        {
            ProcessMode = ProcessDisplayMode.Standard,
            CollapseTiming = ProcessCollapseTiming.NextInput
        };

        var policy = ProcessDisplayPolicy.From(settings);

        Assert.Equal(ProcessCollapseTiming.NextInput, policy.CollapseTiming);
        Assert.Equal(ProcessGrouping.Collapsed, policy.Grouping);
    }

    [Fact]
    public void 没有设置时走标准档()
    {
        var policy = ProcessDisplayPolicy.From(null);

        Assert.Equal(ProcessGrouping.Collapsed, policy.Grouping);
        Assert.Equal(ProcessCollapseTiming.Completion, policy.CollapseTiming);
    }

    [Fact]
    public void 正文是答案其余是过程()
    {
        Assert.Equal(ChatGroupPart.Answer, new ChatStep(ChatStepKind.Text, "答").Part);
        Assert.Equal(ChatGroupPart.Process, new ChatStep(ChatStepKind.Preamble, "过程说明").Part);
        Assert.Equal(ChatGroupPart.Process, new ChatStep(ChatStepKind.Reasoning, "想").Part);
        Assert.Equal(ChatGroupPart.Process, new ChatStep(ChatStepKind.Tool, string.Empty, 0).Part);
        Assert.Equal(ChatGroupPart.Process, new ChatStep(ChatStepKind.HostPrompt, "[宿主提示] x").Part);
    }

    [Fact]
    public void 只有思考与正文会合并工具步骤从不合并()
    {
        var reasoning = new ChatStep(ChatStepKind.Reasoning, "甲");
        Assert.True(reasoning.CanAppend(ChatStepKind.Reasoning));
        Assert.False(reasoning.CanAppend(ChatStepKind.Text));

        var preamble = new ChatStep(ChatStepKind.Preamble, "说明");
        Assert.True(preamble.CanAppend(ChatStepKind.Preamble));
        Assert.False(preamble.CanAppend(ChatStepKind.Text));

        var tool = new ChatStep(ChatStepKind.Tool, string.Empty, 0);
        Assert.False(tool.CanAppend(ChatStepKind.Tool));

        // 合并保留原有元数据（步号 / CallId / 起止时间），只追加文本。
        var stamped = reasoning with { StepIndex = 3, StartedAt = DateTimeOffset.UnixEpoch };
        var merged = stamped.Append("乙");

        Assert.Equal("甲乙", merged.Text);
        Assert.Equal(3, merged.StepIndex);
        Assert.Equal(DateTimeOffset.UnixEpoch, merged.StartedAt);
    }
}

