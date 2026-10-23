using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M1：工具 schema 进入分段预算，报告与界面的数字必须自洽。</summary>
public sealed class ToolBudgetTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    [Fact]
    public void 路由预留计入工具段且满足分段不变量()
    {
        var decision = Decision(estimatedTokens: 600);

        var plan = Builder().Build(new ContextBuildRequest(
            Provider(), "m", [Message()], Routing: decision));

        Assert.Equal(600, plan.Report.Segments.ToolSchema);
        Assert.Equal(600, plan.Report.ToolSchemaTokens);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
        Assert.Equal(decision, plan.Report.Routing);
    }

    [Fact]
    public void 工具段被窗口上限收口()
    {
        var plan = Builder().Build(new ContextBuildRequest(
            Provider(), "m", [Message()], Routing: Decision(estimatedTokens: 99_999)));

        Assert.Equal(plan.Budget.ToolSchemaLimit, plan.Report.Segments.ToolSchema);
        Assert.Equal(1500, plan.Budget.ToolSchemaLimit);
    }

    [Fact]
    public void 没有路由时不占工具段()
    {
        var plan = Builder().Build(new ContextBuildRequest(Provider(), "m", [Message()]));

        Assert.Equal(0, plan.Report.Segments.ToolSchema);
        Assert.Null(plan.Report.Routing);
    }

    [Fact]
    public void 回填实测值后总估算同步修正()
    {
        var decision = Decision(estimatedTokens: 600);
        var plan = Builder().Build(new ContextBuildRequest(
            Provider(), "m", [Message()], Routing: decision));

        var updated = plan.Report.WithRouting(
            900,
            decision with { EstimatedSchemaTokens = 900 });

        Assert.Equal(900, updated.Segments.ToolSchema);
        Assert.Equal(plan.Report.Estimated + 300, updated.Estimated);
        Assert.Equal(updated.Segments.Total, updated.Estimated);
        Assert.Equal(900, updated.Routing!.EstimatedSchemaTokens);
    }

    [Fact]
    public void 预留工具预算会压缩历史消息的空间()
    {
        var profile = ModelProfilePresets.FromPreset("8K")!;
        var budget = ContextBudget.Create(profile, _settings.Current.Context);

        Assert.Equal(1500, budget.ToolSchemaLimit);
        Assert.True(budget.ToolSchemaLimit > 0);
        Assert.True(budget.Total > budget.ToolSchemaLimit);
    }

    private ContextBuilder Builder() => new(_counter, _settings);

    private static RoutingDecision Decision(int estimatedTokens) => new(
        ["read_file", "search_files", "apply_patch", CapabilityRouter.EscapeHatchTool],
        ["read_file", "search_files", "apply_patch"],
        [],
        MaxExposedTools: 6,
        EstimatedSchemaTokens: estimatedTokens,
        EscapeHatch: true,
        Truncated: true,
        Reason: "测试路由");

    private static ChatMessage Message() => new(ChatMessageRole.User, "看一下这个文件");

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
    };
}
