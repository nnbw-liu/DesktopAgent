using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2b「先修剪，后摘要」：修剪档位本身（不调模型，只决定轨迹回放多少）。</summary>
public sealed class ContextPrunerTests
{
    [Fact]
    public void 档位从配置值逐次减半并归零()
    {
        // 300 → 150 → 75 → 0：低于 100 的档位直接归零，不值得再为几十 token 多渲染一次。
        Assert.Equal([300, 150, 75, 0], ContextPruner.TraceTiers(300));
    }

    [Fact]
    public void 第一档永远是原配置_没有配置时只有一档()
    {
        Assert.Equal([0], ContextPruner.TraceTiers(0));
        Assert.Equal([300], ContextPruner.TraceTiers(300).Take(1));
        Assert.False(ContextPruner.CanPrune(0));
    }

    [Fact]
    public void 档位单调不增且以0结尾()
    {
        var tiers = ContextPruner.TraceTiers(120);

        for (var index = 1; index < tiers.Count; index++)
        {
            Assert.True(tiers[index] <= tiers[index - 1], $"第 {index} 档比上一档大");
        }

        Assert.Equal(0, tiers[^1]);
        Assert.True(ContextPruner.CanPrune(120));
    }
}
