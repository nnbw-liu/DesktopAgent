using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>压缩分隔线：条数 / token / 兜底标记，以及点击打开只读原文。</summary>
public sealed class CompactionDividerTests : TestContext
{
    [Fact]
    public void 显示压缩条数与token()
    {
        var cut = RenderComponent<CompactionDivider>(parameters => parameters
            .Add(component => component.Count, 42)
            .Add(component => component.Tokens, 320));

        Assert.Contains("已压缩 42 条历史（约 320 token）", cut.Markup);
        Assert.DoesNotContain("自动摘要", cut.Markup);
    }

    [Fact]
    public void 兜底摘要标注自动摘要()
    {
        var cut = RenderComponent<CompactionDivider>(parameters => parameters
            .Add(component => component.Count, 8)
            .Add(component => component.Tokens, 1200)
            .Add(component => component.IsFallback, true));

        Assert.Contains("已压缩 8 条历史", cut.Markup);
        Assert.Contains("1.2K token", cut.Markup);
        Assert.Contains("· 自动摘要", cut.Markup);
    }

    [Fact]
    public void 点击标签触发打开原文回调()
    {
        var opened = 0;

        var cut = RenderComponent<CompactionDivider>(parameters => parameters
            .Add(component => component.Count, 3)
            .Add(component => component.Tokens, 100)
            .Add(component => component.OnOpen, () => opened++));

        cut.Find(".compaction-label").Click();

        Assert.Equal(1, opened);
    }

    [Fact]
    public void 分隔线与提示文案齐全()
    {
        var cut = RenderComponent<CompactionDivider>();

        Assert.Equal(2, cut.FindAll(".compaction-line").Count);
        Assert.Contains("模型不再看到原文，点击查看", cut.Markup);
    }
}
