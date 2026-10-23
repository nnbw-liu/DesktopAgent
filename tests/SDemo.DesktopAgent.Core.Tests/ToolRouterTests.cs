using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P6 路由：四类意图、Compact 上限 4、最近轨迹加成、Full 不裁剪。</summary>
public sealed class ToolRouterTests
{
    private static readonly HashSet<string> Available =
    [
        "read_file", "search_files", "apply_patch", "run_command", "list_tree", "write_file"
    ];

    [Fact]
    public void 修改意图优先编辑工具()
    {
        var selected = Select("帮我改一下这个文件", AgentMode.Compact);

        Assert.Contains("apply_patch", selected);
        Assert.Contains("read_file", selected);
        Assert.True(selected.Count <= 4);
    }

    [Fact]
    public void 查询意图不选编辑工具()
    {
        var selected = Select("这个函数在哪里", AgentMode.Compact);

        Assert.Contains("search_files", selected);
        Assert.DoesNotContain("apply_patch", selected);
    }

    [Fact]
    public void 运行意图优先run_command()
    {
        var selected = Select("跑一下构建", AgentMode.Compact);

        Assert.Equal("run_command", selected[0]);
    }

    [Fact]
    public void 未知意图给保守四件套()
    {
        var selected = Select("嗯", AgentMode.Compact);

        Assert.Equal(["list_tree", "read_file", "search_files", "run_command"], selected);
    }

    [Fact]
    public void 最近工具优先复用()
    {
        var context = new ToolSelectionContext(
            "改代码",
            ["search_files"],
            Available,
            AgentMode.Compact);

        var selected = ToolRouter.Select(context);

        Assert.Equal("search_files", selected[0]);
    }

    [Fact]
    public void Full模式返回全部可用工具()
    {
        var selected = Select("随便", AgentMode.Full);

        Assert.Equal(Available.Count, selected.Count);
    }

    private static IReadOnlyList<string> Select(string input, AgentMode mode) =>
        ToolRouter.Select(new ToolSelectionContext(input, [], Available, mode));
}
