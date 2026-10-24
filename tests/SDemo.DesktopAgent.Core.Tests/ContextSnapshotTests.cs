using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P2a 验收（迁移文档 §3.3.1-(3)）：
/// required 段永不消失、快照字节稳定、归因可见、单段可关成本为 0、切换策略会被看见。
/// </summary>
public sealed class ContextSnapshotTests
{
    private static readonly DateTimeOffset FrozenNow = new(
        new DateTime(2026, 10, 24, 23, 44, 4),
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 24, 23, 44, 4)));

    private readonly HeuristicTokenCounter _counter = new();
    private readonly StubSettingsService _settings = new();
    private readonly FixedClock _clock = new(FrozenNow);

    private ContextBuilder CreateBuilder() => new(_counter, _settings, time: _clock);

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
    };

    private static List<ChatMessage> Messages() =>
    [
        new(ChatMessageRole.User, "第一轮"),
        new(ChatMessageRole.Assistant, "收到"),
        new(ChatMessageRole.User, "第二轮")
    ];

    private static WorkspaceState State(string verification = "dotnet build → ok(0)") => new(
        Root: @"C:\Code\Demo",
        Tree: Enumerable.Range(0, 60).Select(index => $"src/module{index}/").ToList(),
        RecentEdits: [new RecentEdit("src/a.cs", "3f9a", FrozenNow.DateTime, "write_file")],
        RecentActions: [new RecentAction(FrozenNow.DateTime, "[工具] write_file src/a.cs → ok")],
        LastVerification: verification,
        PinnedCount: 0,
        Todos: [new TodoItem("写测试", TodoStatus.InProgress)]);

    private ContextPlan Build(WorkspaceState? state = null) =>
        CreateBuilder().Build(new ContextBuildRequest(
            Provider(),
            "m",
            Messages(),
            State: state ?? State()));

    [Fact]
    public void 可选段全关且预算压到200时必需段仍在且未截断()
    {
        _settings.Current.Context.StatePackEnabled = false;
        _settings.Current.Context.PlanContextEnabled = false;
        _settings.Current.Context.TimeContextEnabled = false;
        // 预算压到 200：可选段一个都放不下，必需段照旧。
        _settings.Current.Context.StatePackSmallModelTokenBudget = 200;

        var plan = Build();
        var sections = plan.Report.SectionUsages;

        var policy = sections.Single(section => section.Name == "policy");
        var verification = sections.Single(section => section.Name == "verification");

        Assert.True(policy.Required);
        Assert.False(policy.Truncated);
        Assert.True(verification.Required);
        Assert.False(verification.Truncated);

        var snapshot = plan.Messages[^1].Text;
        Assert.Contains("当前策略", snapshot);
        Assert.Contains("### 验证", snapshot);
        Assert.Contains("dotnet build → ok(0)", snapshot);
        // 可选段整段消失 = 成本严格为 0，不是渲染空标题。
        Assert.Equal(0, sections.Where(section => !section.Required).Sum(section => section.Tokens));
    }

    [Fact]
    public void 同一会话连续五轮渲染逐字节相同()
    {
        var builder = CreateBuilder();
        var texts = new List<string>();

        for (var round = 0; round < 5; round++)
        {
            // 每一轮都重建请求：历史、工作区事实与时钟都不变。
            var plan = builder.Build(new ContextBuildRequest(
                Provider(),
                "m",
                Messages(),
                State: State()));
            texts.Add(plan.Messages[^1].Text);
        }

        Assert.All(texts, text => Assert.Equal(texts[0], text));
    }

    [Fact]
    public void 关掉时间与计划段后成本为0()
    {
        var withSections = Build();

        _settings.Current.Context.TimeContextEnabled = false;
        _settings.Current.Context.PlanContextEnabled = false;
        var withoutSections = Build();

        Assert.Contains(withSections.Report.SectionUsages, section => section.Name == "time");
        Assert.Contains(withSections.Report.SectionUsages, section => section.Name == "plan");
        Assert.DoesNotContain(withoutSections.Report.SectionUsages, section => section.Name == "time");
        Assert.DoesNotContain(withoutSections.Report.SectionUsages, section => section.Name == "plan");
        Assert.DoesNotContain("当前时间", withoutSections.Messages[^1].Text);
        Assert.DoesNotContain("当前计划", withoutSections.Messages[^1].Text);

        Assert.True(withoutSections.Report.StatePackTokens < withSections.Report.StatePackTokens);
    }

    [Fact]
    public void 切换直跑与只读后策略段文本随之变化()
    {
        var before = Build().Messages[^1].Text;

        _settings.Current.Execution.Mode = ExecutionMode.AllowlistDirect;
        _settings.Current.Execution.DirectRunEnabled = true;
        _settings.Current.Execution.Allowlist = ["dotnet", "git"];
        _settings.Current.Sandbox.WorkspaceReadOnly = true;
        _settings.Current.Sandbox.Requirement = SandboxRequirement.Preferred;
        _settings.Current.ConfirmFileWrite = false;

        var after = Build().Messages[^1].Text;

        Assert.NotEqual(before, after);
        Assert.Contains("禁止（命令一律在沙箱内执行）", before);
        Assert.Contains("需要用户在界面确认后才生效", before);
        Assert.Contains("允许白名单命令在宿主直跑", after);
        Assert.Contains("命令白名单：dotnet, git", after);
        Assert.Contains("直接生效（已关闭逐次确认）", after);
        Assert.Contains("工作区副本只读", after);
        Assert.Contains("可降级", after);
    }

    [Fact]
    public void 时间读数带日期时区与经过时长()
    {
        // 没有历史消息 = 没有「上一条模型可见消息」，此时必须照实写 unavailable，不要编一个数字。
        var plan = CreateBuilder().Build(new ContextBuildRequest(
            Provider(),
            "m",
            [],
            State: State()));

        var text = plan.Messages[^1].Text;

        Assert.Contains("宿主时钟：2026-10-24T23:44:04", text);
        Assert.Contains("距上一条模型可见消息：unavailable", text);

        // 有历史时给相对时长，而不是只给一个 HH:mm。
        var withHistory = Build(State()).Messages[^1].Text;
        Assert.DoesNotContain("距上一条模型可见消息：unavailable", withHistory);
        Assert.Matches("距上一条模型可见消息：(刚刚|\\d+ 分钟|\\d+ 小时.*|\\d+ 天)", withHistory);
    }

    [Fact]
    public void 快照永远在消息列表最后一条且角色是user()
    {
        var plan = Build();

        var last = plan.Messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, last.Role);
        Assert.StartsWith("## 宿主上下文快照", last.Text);
        Assert.Contains("本快照取代之前的快照", last.Text);
    }

    [Fact]
    public void 快照预算从历史消息预算里扣掉()
    {
        var plan = Build(State() with { Tree = [] });

        Assert.True(plan.Report.StatePackUsed);
        Assert.True(plan.Report.Segments.StatePack > 0);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
        Assert.True(plan.Report.Estimated <= plan.Report.Total);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }
}
