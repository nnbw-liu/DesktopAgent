using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>分段明细：10 个分段都渲染、各段之和与总估算一致、压缩 / 路由 / 裁剪提示可见。</summary>
public sealed class ContextBreakdownTests : TestContext
{
    [Fact]
    public void 渲染全部分段并与总估算一致()
    {
        var report = Report();

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        foreach (var label in new[]
                 {
                     "系统提示词", "技能", "工具 schema", "工作区知识库", "历史对话",
                     "已压缩摘要", "上下文快照", "附件", "工具轨迹", "Pin 消息"
                 })
        {
            Assert.Contains(label, cut.Markup);
        }

        // 条带宽度之和 ≈ 100%（各段之和 = Estimated 的不变量）。
        var widths = cut.FindAll(".context-bar-part")
            .Select(element => element.GetAttribute("style") ?? string.Empty)
            .Select(style => double.Parse(style.Split(':')[1].Trim().TrimEnd('%')))
            .ToList();

        Assert.Equal(10, widths.Count);
        Assert.InRange(widths.Sum(), 99.0, 101.0);
    }

    [Fact]
    public void 没有报告时不渲染()
    {
        var cut = RenderComponent<ContextBreakdown>();

        Assert.Empty(cut.FindAll(".context-breakdown"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void 压缩摘要显示条数与成本()
    {
        var report = Report() with
        {
            SummaryUsed = true,
            CompactedMessages = 42,
            SummaryTokens = 320,
            SummaryInputTokens = 1800,
            SummaryOutputTokens = 320
        };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.Contains("已压缩 42 条历史", cut.Markup);
        Assert.Contains("摘要成本（累计）：输入 1.8K / 输出 320 token", cut.Markup);
    }

    [Fact]
    public void 摘要未调用模型时不显示成本行()
    {
        var report = Report() with { SummaryUsed = true, CompactedMessages = 4 };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.DoesNotContain("摘要成本", cut.Markup);
    }

    [Fact]
    public void 路由裁剪与溢出标记可见()
    {
        var report = Report() with
        {
            OmittedMessages = 6,
            OmittedTokens = 900,
            AttachmentsStubbed = true,
            PinnedTruncated = true,
            MidTurnTrimmed = true,
            OverflowRetried = true,
            Routing = new RoutingDecision(
                ["read_file", "search_files", "search_capabilities"],
                ["read_file", "search_files"],
                [],
                MaxExposedTools: 6,
                EstimatedSchemaTokens: 800,
                EscapeHatch: true,
                Truncated: true,
                Reason: "Full · 意图=edit · 暴露 3/12（上限 6）")
            {
                Source = RoutingDecision.RulesSource
            }
        };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.Contains("已省略 6 条较早消息", cut.Markup);
        Assert.Contains("部分附件只保留文件名", cut.Markup);
        Assert.Contains("部分 pin 消息已截断", cut.Markup);
        Assert.Contains("中途裁剪", cut.Markup);
        Assert.Contains("溢出重试", cut.Markup);
        Assert.Contains("工具路由（规则）", cut.Markup);
        Assert.Contains("含兜底 search_capabilities", cut.Markup);
    }

    [Fact]
    public void 快照按段渲染名字与token且标出必需与截断()
    {
        var report = Report() with
        {
            StatePackUsed = true,
            StatePackTokens = 220,
            Sections =
            [
                new ContextSectionUsage("workspace", "工作区", 100, Required: false, Tokens: 60, Truncated: true),
                new ContextSectionUsage("policy", "当前策略", 110, Required: true, Tokens: 90, Truncated: false),
                new ContextSectionUsage("time", "当前时间", 150, Required: true, Tokens: 40, Truncated: false)
            ]
        };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.Contains("· 工作区", cut.Markup);
        Assert.Contains("· 当前策略", cut.Markup);
        Assert.Contains("· 当前时间", cut.Markup);
        Assert.Contains("必需", cut.Markup);
        Assert.Contains("可选 · 已截断", cut.Markup);
        Assert.Contains("3 段", cut.Markup);
        Assert.Contains("宿主上下文快照约 220 token", cut.Markup);
    }

    [Fact]
    public void 服务端上报缓存命中时显示命中率()
    {
        var report = Report() with
        {
            ActualInputTokens = 1000,
            CacheHitTokens = 800
        };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.Contains("缓存命中 800（80%）", cut.Markup);
    }

    [Fact]
    public void 端点不报缓存时不显示命中率()
    {
        var report = Report() with { ActualInputTokens = 1000 };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.DoesNotContain("缓存命中", cut.Markup);
    }

    [Fact]
    public void 工具schema行带路由数量()
    {        var report = Report() with
        {
            Segments = Report().Segments with { ToolSchema = 1200 },
            Routing = new RoutingDecision(
                ["read_file", "search_files"],
                ["read_file", "search_files"],
                [],
                MaxExposedTools: 6,
                EstimatedSchemaTokens: 1200,
                EscapeHatch: false,
                Truncated: false,
                Reason: "规则")
        };

        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, report));

        Assert.Contains("2 个", cut.Markup);
    }

    [Fact]
    public void 修剪轨迹省下的量单独列出()
    {
        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, Report() with { PrunedTokens = 420 }));

        Assert.Contains("已先修剪工具轨迹约 420 token", cut.Markup);
        Assert.Contains("不必压缩历史", cut.Markup);
    }

    [Fact]
    public void 没有修剪时不显示修剪行()
    {
        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, Report()));

        Assert.DoesNotContain("已先修剪工具轨迹", cut.Markup);
    }

    [Fact]
    public void 超长工具输出落盘后显示条数与回读说明()
    {
        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, Report() with { SpilledResults = 3, SpillFailures = 1 }));

        Assert.Contains("已把 3 条超长工具输出落盘到 .agent/spill", cut.Markup);
        Assert.Contains("1 条写盘失败，已退回截断", cut.Markup);
    }

    [Fact]
    public void 没有落盘时不显示落盘说明()
    {
        var cut = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, Report()));

        Assert.DoesNotContain(".agent/spill", cut.Markup);
    }

    private static ContextReport Report() => new(
        ProviderId: "test",
        ModelId: "test",
        ContextWindow: 32_768,
        ReservedOutput: 4096,
        Safety: 1000,
        Total: 20_000,
        FixedCap: 8000,
        Estimated: 5000,
        RawEstimated: 5200,
        CalibrationFactor: 1.05,
        Segments: new ContextSegments(
            System: 1500,
            Skills: 300,
            Turns: 2000,
            Attachments: 200,
            ToolTrace: 300,
            Pinned: 500,
            Summary: 100,
            StatePack: 60,
            ToolSchema: 40,
            Knowledge: 0),
        OmittedMessages: 0,
        OmittedTokens: 0,
        KeptTurns: 4,
        TotalTurns: 6,
        PinnedCount: 1,
        PinnedTruncated: false,
        AttachmentsStubbed: false,
        Trimmed: false);
}
