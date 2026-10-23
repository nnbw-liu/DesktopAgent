using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>上下文占用环：阈值分档、数字格式、点开明细。</summary>
public sealed class ContextMeterTests : TestContext
{
    public ContextMeterTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void 低于80不显示告警档()
    {
        var cut = RenderComponent<ContextMeter>(parameters => parameters
            .Add(component => component.Report, Report(790, 1000)));

        Assert.DoesNotContain("is-warn", cut.Markup);
        Assert.DoesNotContain("is-danger", cut.Markup);
        Assert.Contains("790 / 1.0K", cut.Markup);
    }

    [Fact]
    public void 超过80显示告警档()
    {
        var cut = RenderComponent<ContextMeter>(parameters => parameters
            .Add(component => component.Report, Report(850, 1000)));

        Assert.Contains("is-warn", cut.Markup);
        Assert.DoesNotContain("is-danger", cut.Markup);
    }

    [Fact]
    public void 超过95显示危险档()
    {
        var cut = RenderComponent<ContextMeter>(parameters => parameters
            .Add(component => component.Report, Report(960, 1000)));

        Assert.Contains("is-danger", cut.Markup);
    }

    [Fact]
    public void 点击展开分段明细再点关闭()
    {
        var cut = RenderComponent<ContextMeter>(parameters => parameters
            .Add(component => component.Report, Report(500, 1000)));

        Assert.Empty(cut.FindAll(".context-popover"));

        cut.Find(".context-meter").Click();

        Assert.Single(cut.FindAll(".context-popover"));
        Assert.Contains("系统提示词", cut.Markup);

        cut.Find(".context-popover-close").Click();

        Assert.Empty(cut.FindAll(".context-popover"));
    }

    [Fact]
    public void 没有报告时禁用且不显示数字()
    {
        var cut = RenderComponent<ContextMeter>();

        var button = cut.Find(".context-meter");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Contains("—", cut.Markup);
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(123_456, "123K")]
    public void 数字格式按量级缩写(int tokens, string expected)
    {
        // Format 是 internal static：测试程序集通过 InternalsVisibleTo 访问。
        Assert.Equal(expected, ContextMeter.Format(tokens));
    }

    internal static ContextReport Report(int estimated, int total) => new(
        ProviderId: "test",
        ModelId: "test",
        ContextWindow: total * 2,
        ReservedOutput: 500,
        Safety: 100,
        Total: total,
        FixedCap: total / 4,
        Estimated: estimated,
        RawEstimated: estimated,
        CalibrationFactor: 1.0,
        Segments: new ContextSegments(
            System: estimated / 4,
            Skills: estimated / 4,
            Turns: estimated / 4,
            Attachments: 0,
            ToolTrace: 0,
            Pinned: estimated - (estimated / 4 * 3)),
        OmittedMessages: 0,
        OmittedTokens: 0,
        KeptTurns: 2,
        TotalTurns: 2,
        PinnedCount: 0,
        PinnedTruncated: false,
        AttachmentsStubbed: false,
        Trimmed: false);
}
