using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P2/P2a 构建器接线：摘要留在固定块、工作区状态改走末尾的宿主上下文快照、
/// 被摘要覆盖的消息不再发送、pin 例外。
/// </summary>
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

    private static WorkspaceState State(string verification = "未运行验证") => new(
        Root: @"C:\Demo",
        Tree: ["src/", "README.md"],
        RecentEdits: [],
        RecentActions: [],
        LastVerification: verification,
        PinnedCount: 0);

    private ContextPlan Build(
        List<ChatMessage> messages,
        string? summary = null,
        int throughOrder = 0,
        WorkspaceState? state = null,
        bool passthrough = false) => new ContextBuilder(_counter, _settings).Build(new ContextBuildRequest(
        Provider(),
        "m",
        messages,
        Passthrough: passthrough,
        SummaryText: summary,
        SummaryThroughOrder: throughOrder,
        State: state));

    [Fact]
    public void 摘要留在固定块而工作区状态进末尾快照()
    {
        var plan = Build(Messages(), Summary(), throughOrder: 4, state: State());

        Assert.True(plan.Report.SummaryUsed);
        Assert.True(plan.Report.StatePackUsed);
        Assert.Equal(4, plan.Report.CompactedMessages);
        Assert.True(plan.Report.Segments.Summary > 0);
        Assert.True(plan.Report.Segments.StatePack > 0);

        // 摘要仍在系统提示词里；工作区状态搬到了最后一条 user 消息（P2a §3.3.1-(2)）。
        Assert.Contains("已压缩的历史", plan.Instructions);
        Assert.Contains("不要修改数据库", plan.Instructions);
        Assert.DoesNotContain("根目录", plan.Instructions);

        var snapshot = plan.Messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, snapshot.Role);
        Assert.Contains("宿主上下文快照", snapshot.Text);
        Assert.Contains("根目录", snapshot.Text);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
    }

    [Fact]
    public void 快照按段归因并且带策略与验证()
    {
        var plan = Build(Messages(), state: State("dotnet build → ok(0)"));

        var sections = plan.Report.SectionUsages;
        Assert.Contains(sections, section => section.Name == "policy" && section.Required);
        Assert.Contains(sections, section => section.Name == "verification" && section.Required);
        Assert.Contains(sections, section => section.Name == "time" && section.Required);
        Assert.All(sections, section => Assert.True(section.Tokens > 0));

        Assert.Contains("当前策略", plan.Messages[^1].Text);
        Assert.Contains("dotnet build → ok(0)", plan.Messages[^1].Text);

        // 分段之和 = 各段正文；快照整体还多一行消息头，两者差在个位数量级。
        var sectionTotal = sections.Sum(section => section.Tokens);
        Assert.True(plan.Report.StatePackTokens > sectionTotal);
        Assert.InRange(plan.Report.StatePackTokens - sectionTotal, 1, 40);
    }

    [Fact]
    public void 被摘要覆盖的消息不再发送()
    {
        var plan = Build(Messages(), Summary(), throughOrder: 4);

        // 4 条历史 + 1 条快照。
        Assert.Equal(5, plan.Messages.Count);
        Assert.DoesNotContain(plan.Messages, message => message.Text.Contains("消息 0", StringComparison.Ordinal));
        Assert.Contains(plan.Messages, message => message.Text.Contains("消息 4", StringComparison.Ordinal));
    }

    [Fact]
    public void pin的消息即使被摘要覆盖也保留原文()
    {
        var messages = Messages();
        messages[1].IsPinned = true;

        var plan = Build(messages, Summary(), throughOrder: 4);

        Assert.Equal(6, plan.Messages.Count);
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
    public void 直通模式忽略摘要与快照()
    {
        var plan = Build(Messages(), Summary(), throughOrder: 4, state: State(), passthrough: true);

        Assert.False(plan.Report.SummaryUsed);
        Assert.False(plan.Report.StatePackUsed);
        Assert.DoesNotContain("已压缩的历史", plan.Instructions);
        Assert.Equal(8, plan.Messages.Count);
        Assert.DoesNotContain(plan.Messages, message => message.Text.Contains("宿主上下文快照", StringComparison.Ordinal));
    }

    [Fact]
    public void 没有摘要时摘要分段为零()
    {
        var plan = Build(Messages());

        Assert.False(plan.Report.SummaryUsed);
        Assert.Equal(0, plan.Report.Segments.Summary);
        Assert.Equal(0, plan.Report.CompactedMessages);
    }

    [Fact]
    public void 没有工作区时快照仍带策略与时间()
    {
        var plan = Build(Messages());

        Assert.True(plan.Report.StatePackUsed);
        Assert.DoesNotContain(plan.Report.SectionUsages, section => section.Name == "workspace");
        Assert.Contains(plan.Report.SectionUsages, section => section.Name == "policy");
        Assert.Contains(plan.Report.SectionUsages, section => section.Name == "time");
    }
}
