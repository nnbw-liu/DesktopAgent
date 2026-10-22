using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>循环保护：连续三次提醒、八次上限、不同参数重置。</summary>
public sealed class ToolCallGuardTests
{
    [Fact]
    public void 连续三次同参调用给出提醒()
    {
        var guard = new ToolCallGuard();

        Assert.Null(guard.Check("read_file", "a").Message);
        Assert.Null(guard.Check("read_file", "a").Message);
        var third = guard.Check("read_file", "a");

        Assert.True(third.Allowed);
        Assert.Contains("连续出现 3 次", third.Message);
    }

    [Fact]
    public void 参数变化后计数重置()
    {
        var guard = new ToolCallGuard();
        guard.Check("read_file", "a");
        guard.Check("read_file", "a");

        Assert.Null(guard.Check("read_file", "b").Message);
    }

    [Fact]
    public void 超过八次拒绝继续调用()
    {
        var guard = new ToolCallGuard();

        for (var index = 1; index <= ToolCallGuard.MaxCalls; index++)
        {
            Assert.True(guard.Check("read_file", $"path-{index}").Allowed);
        }

        var blocked = guard.Check("read_file", "path-9");

        Assert.False(blocked.Allowed);
        Assert.Equal(1, guard.LoopsPrevented);
        Assert.Contains("上限", blocked.Message);
    }
}
