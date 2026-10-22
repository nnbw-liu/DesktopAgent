using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 摘要格式：6 节齐全、顺序固定、来源序号与 token 上限。</summary>
public sealed class SummaryFormatTests
{
    private readonly HeuristicTokenCounter _counter = new();

    private static string ValidSummary() => """
        ## 目标与约束
        - 不要修改数据库结构 [m1]
        ## 已确认决策
        - 使用 docs/ 目录 [m2]
        ## 关键事实
        - 构建命令是 dotnet build [m3]
        ## 已改动
        - src/a.cs [m4]
        ## 未完成
        - 无
        ## 用户偏好
        - 无
        """;

    [Fact]
    public void 六节齐全且带来源时通过()
    {
        var validation = SummaryFormat.Validate(ValidSummary(), 800, _counter);

        Assert.True(validation.Ok, validation.Reason);
    }

    [Fact]
    public void 缺少小节时失败()
    {
        var text = ValidSummary().Replace("## 用户偏好", "## 其它");

        var validation = SummaryFormat.Validate(text, 800, _counter);

        Assert.False(validation.Ok);
        Assert.Contains("小节", validation.Reason);
    }

    [Fact]
    public void 小节顺序错误时失败()
    {
        var text = ValidSummary()
            .Replace("## 目标与约束", "## 已确认决策")
            .Replace("## 已确认决策\n- 使用 docs/ 目录 [m2]", "## 目标与约束\n- 使用 docs/ 目录 [m2]")
            .Replace("## 目标与约束\n- 不要修改数据库结构 [m1]", "## 已确认决策\n- 不要修改数据库结构 [m1]");

        var validation = SummaryFormat.Validate(text, 800, _counter);

        Assert.False(validation.Ok);
        Assert.Contains("顺序", validation.Reason);
    }

    [Fact]
    public void 事实缺少来源序号时失败()
    {
        var text = ValidSummary().Replace("- 使用 docs/ 目录 [m2]", "- 使用 docs/ 目录");

        var validation = SummaryFormat.Validate(text, 800, _counter);

        Assert.False(validation.Ok);
        Assert.Contains("来源序号", validation.Reason);
    }

    [Fact]
    public void 出现模板之外的小节时失败()
    {
        var text = ValidSummary() + "\n## 额外建议\n- 无";

        var validation = SummaryFormat.Validate(text, 800, _counter);

        Assert.False(validation.Ok);
    }

    [Fact]
    public void 超长摘要按行截断到上限()
    {
        var text = ValidSummary() + "\n" + string.Join('\n', Enumerable.Repeat("- 填充事实 [m9]", 200));

        var capped = SummaryFormat.Cap(text, 200, _counter);

        Assert.True(_counter.Count(capped) <= 200);
        Assert.Contains("摘要已截断", capped);
    }

    [Fact]
    public void 兜底标记可识别且来源序号去重排序()
    {
        Assert.True(SummaryFormat.IsFallback(SummaryFormat.FallbackMarker + "\n## 目标与约束"));
        Assert.False(SummaryFormat.IsFallback(ValidSummary()));

        var sources = SummaryFormat.Sources("事实 [m12] 另一条 [m3] 重复 [m12]");
        Assert.Equal([3, 12], sources);
    }
}
