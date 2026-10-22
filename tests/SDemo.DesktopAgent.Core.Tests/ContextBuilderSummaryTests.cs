using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 构建器接线：摘要 / 状态包进入固定块、被摘要覆盖的消息不再发送、pin 例外。</summary>
public sealed class ContextBuilderSummaryTests
{
    private readonly HeuristicTokenCounter _counter = new();
    private readonly StubSettingsService _settings = new();

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
    };

    private static string Summary() => """
        ## 目标与约束
        - 不要修改数据库 [m1]
        ## 已确认决策
        - 无
        ## 关键事实
        - 无
        ## 已改动
        - src/a.cs [m3]
        ## 未完成
        - 无
        ## 用户偏好
        - 无
        """;

    private static List<ChatMessage> Messages(int count = 8) =>
        Enumerable.Range(0, count)
            .Select(index => new ChatMessage(
                index % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                $"消息 {index}"))
            .ToList();

    private ContextPlan Build(
        List<ChatMessage> messages,
        string? summary = null,
        int throughOrder = 0,
        string? statePack = null,
        bool passthrough = false) => new ContextBuilder(_counter, _settings).Build(new ContextBuildRequest(
        Provider(),
        "m",
        messages,
        Passthrough: passthrough,
        SummaryText: summary,
        SummaryThroughOrder: throughOrder,
        StatePackText: statePack));

    [Fact]
    public void 摘要与状态包进入固定块并分段计量()
    {
        var plan = Build(
            Messages(),
            Summary(),
            throughOrder: 4,
            statePack: "## 工作区\n根目录：C:\\Demo\n## 验证\n未运行验证");

        Assert.True(plan.Report.SummaryUsed);
        Assert.True(plan.Report.StatePackUsed);
        Assert.Equal(4, plan.Report.CompactedMessages);
        Assert.True(plan.Report.Segments.Summary > 0);
        Assert.True(plan.Report.Segments.StatePack > 0);
        Assert.Contains("已压缩的历史", plan.Instructions);
        Assert.Contains("不要修改数据库", plan.Instructions);
        Assert.Contains("工作区状态", plan.Instructions);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
    }

    [Fact]
    public void 被摘要覆盖的消息不再发送()
    {
        var plan = Build(Messages(), Summary(), throughOrder: 4);

        Assert.Equal(4, plan.Messages.Count);
        Assert.DoesNotContain(plan.Messages, message => message.Text.Contains("消息 0", StringComparison.Ordinal));
        Assert.Contains(plan.Messages, message => message.Text.Contains("消息 4", StringComparison.Ordinal));
    }

    [Fact]
    public void pin的消息即使被摘要覆盖也保留原文()
    {
        var messages = Messages();
        messages[1].IsPinned = true;

        var plan = Build(messages, Summary(), throughOrder: 4);

        Assert.Equal(5, plan.Messages.Count);
        Assert.Contains(plan.Messages, message => message.Text.Contains("消息 1", StringComparison.Ordinal));
    }

    [Fact]
    public void 摘要超长时按设置上限截断()
    {
        _settings.Current.Context.SummaryMaxTokens = 120;
        var huge = "## 目标与约束\n" + string.Join('\n', Enumerable.Repeat("- 事实事实事实 [m1]", 400));

        var plan = Build(Messages(), huge, throughOrder: 4);

        Assert.True(plan.Report.Segments.Summary <= 120 + 40);
        Assert.Contains("摘要已截断", plan.Instructions);
    }

    [Fact]
    public void 直通模式忽略摘要与状态包()
    {
        var plan = Build(Messages(), Summary(), throughOrder: 4, statePack: "## 工作区", passthrough: true);

        Assert.False(plan.Report.SummaryUsed);
        Assert.False(plan.Report.StatePackUsed);
        Assert.DoesNotContain("已压缩的历史", plan.Instructions);
        Assert.Equal(8, plan.Messages.Count);
    }

    [Fact]
    public void 没有摘要时分段为零()
    {
        var plan = Build(Messages());

        Assert.False(plan.Report.SummaryUsed);
        Assert.False(plan.Report.StatePackUsed);
        Assert.Equal(0, plan.Report.Segments.Summary);
        Assert.Equal(0, plan.Report.Segments.StatePack);
        Assert.Equal(0, plan.Report.CompactedMessages);
    }
}
