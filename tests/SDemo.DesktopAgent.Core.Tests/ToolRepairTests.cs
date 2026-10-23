using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P6 工具参数修复：围栏、前后废话、尾逗号、单引号、类型纠正、失败止损。</summary>
public sealed class ToolRepairTests
{
    [Fact]
    public void 去json围栏()
    {
        var result = ToolCallRepair.Repair("```json\n{\"path\":\"a.cs\"}\n```");

        Assert.True(result.Success);
        Assert.Contains("\"path\"", result.Json);
    }

    [Fact]
    public void 截取前后废话中的平衡对象()
    {
        var result = ToolCallRepair.Repair("好的，参数如下：{\"path\":\"a.cs\",\"limit\":3} 请执行");

        Assert.True(result.Success);
        Assert.Contains("a.cs", result.Json);
    }

    [Fact]
    public void 尾逗号可以修复()
    {
        var result = ToolCallRepair.Repair("{\"path\":\"a.cs\",}");

        Assert.True(result.Success);
    }

    [Fact]
    public void 单引号可以修复()
    {
        var result = ToolCallRepair.Repair("{'path':'a.cs'}");

        Assert.True(result.Success);
        Assert.Contains("a.cs", result.Json);
    }

    [Fact]
    public void 字符串数字与布尔被纠正()
    {
        var result = ToolCallRepair.Repair("{\"limit\":\"3\",\"recursive\":\"true\"}");

        Assert.True(result.Success);
        Assert.Contains("\"limit\":3", result.Json);
        Assert.Contains("\"recursive\":true", result.Json);
    }

    [Fact]
    public void 缺括号或非JSON时失败()
    {
        Assert.False(ToolCallRepair.Repair("not json").Success);
        Assert.False(ToolCallRepair.Repair("{\"path\":\"a.cs\"").Success);
        Assert.False(ToolCallRepair.Repair("").Success);
    }
}
