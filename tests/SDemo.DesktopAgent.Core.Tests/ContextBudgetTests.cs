using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>预算公式（§3.4）：预留输出、安全余量、总预算与固定块上限。</summary>
public sealed class ContextBudgetTests
{
    [Fact]
    public void 窗口为8K时按公式得到预算()
    {
        var budget = ContextBudget.Create(new ModelProfile(8192, 2048), new ContextSettings());

        // reserve = min(2048, max(1024, 2048)) = 2048
        // safety  = max(256, 409) = 409
        // total   = floor((8192 - 2048 - 409) * 0.85) = 4874
        Assert.Equal(2048, budget.ReservedOutput);
        Assert.Equal(409, budget.Safety);
        Assert.Equal(4874, budget.Total);
        Assert.Equal(1949, budget.FixedCap);
        Assert.Equal(2924, budget.CompactTarget);
    }

    [Fact]
    public void 小窗口的输出预留被夹到窗口四分之一()
    {
        var budget = ContextBudget.Create(new ModelProfile(4096, 2048), new ContextSettings());

        // reserve = min(2048, 1024)；available = 4096 - 1024 - 256 = 2816。
        Assert.Equal(1024, budget.ReservedOutput);
        Assert.Equal(256, budget.Safety);
        Assert.Equal(2393, budget.Total);
        Assert.True(budget.Total < budget.Window);
    }

    [Fact]
    public void 预算比例被夹在合理范围()
    {
        var loose = ContextBudget.Create(new ModelProfile(8192, 2048), new ContextSettings { BudgetRatio = 2.0 });
        var tight = ContextBudget.Create(new ModelProfile(8192, 2048), new ContextSettings { BudgetRatio = 0 });

        // 5735 * 0.95 = 5448；5735 * 0.10 = 573。
        Assert.Equal(5448, loose.Total);
        Assert.Equal(573, tight.Total);
    }

    [Fact]
    public void Scale只缩小预算并保留窗口信息()
    {
        var budget = ContextBudget.Create(new ModelProfile(8192, 2048), new ContextSettings());
        var scaled = budget.Scale(0.5);

        Assert.Equal(2437, scaled.Total);
        Assert.Equal(974, scaled.FixedCap);
        Assert.Equal(1462, scaled.CompactTarget);
        Assert.Equal(budget.Window, scaled.Window);
        Assert.Equal(budget.ReservedOutput, scaled.ReservedOutput);
        Assert.True(scaled.Total < budget.Total);
    }

    [Fact]
    public void 固定块上限不超过总预算()
    {
        var settings = new ContextSettings { FixedCapRatio = 0.9 };
        var budget = ContextBudget.Create(new ModelProfile(32_768, 4096), settings);

        Assert.True(budget.FixedCap <= budget.Total);
        Assert.True(budget.CompactTarget <= budget.Total);
        Assert.True(budget.Total > 0);
    }
}
