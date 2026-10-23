using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>一次生成的轨迹保留上限：不能在第 20 条把后续编辑摘要截掉。</summary>
public sealed class AgentRunMetricsTests
{
    [Fact]
    public void 超过二十条轨迹仍保留到存储上限()
    {
        var metrics = new AgentRunMetrics();

        for (var index = 0; index < 21; index++)
        {
            metrics.RecordToolTrace(Entry(index));
        }

        Assert.Equal(21, metrics.ToolTrace.Count);
        Assert.Equal("apply_patch", metrics.ToolTrace[^1].ToolId);
    }

    [Fact]
    public void 存储上限生效()
    {
        var metrics = new AgentRunMetrics();

        for (var index = 0; index < AgentRunMetrics.MaxStoredEntries + 5; index++)
        {
            metrics.RecordToolTrace(Entry(index));
        }

        Assert.Equal(AgentRunMetrics.MaxStoredEntries, metrics.ToolTrace.Count);
    }

    private static ToolTraceEntry Entry(int index) =>
        new(
            index == 20 ? "apply_patch" : "read_file",
            ToolTraceEntry.StatusOk,
            null,
            $"file{index}.cs",
            "ok",
            10,
            DateTime.Now);
}
