using System.Text.Json;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>改动摘要：从工具结果解析、按路径聚合、失败不计数、持久化。</summary>
public sealed class EditSummaryTests
{
    [Fact]
    public void 解析工具结果里的改动行()
    {
        var text = "已修改 2 个文件：a.cs (+2 -1)、b.cs (+1 -0)\n[EDIT] a.cs +2 -1\n[EDIT] b.cs +1 -0";

        var edits = AgentChatService.ParseEdits(text);

        Assert.NotNull(edits);
        Assert.Equal(2, edits!.Count);
        Assert.Equal("a.cs", edits[0].Path);
        Assert.Equal(2, edits[0].Added);
        Assert.Equal(1, edits[0].Removed);
    }

    [Fact]
    public void 没有改动标记时返回空()
    {
        Assert.Null(AgentChatService.ParseEdits("未写入任何文件：a.cs 第 1 个 hunk 未匹配"));
        Assert.Null(AgentChatService.ParseEdits(null));
    }

    [Fact]
    public void 同一路径多次改动按次数累加()
    {
        var trace = new[]
        {
            Entry("a.cs", 2, 1),
            Entry("a.cs", 1, 0),
            Entry("b.cs", 3, 2)
        };

        var summary = EditSummary.FromTrace(trace);

        Assert.Equal(2, summary.Files.Count);
        Assert.Equal(3, summary.Files.Single(file => file.Path == "a.cs").Added);
        Assert.Equal(1, summary.Files.Single(file => file.Path == "a.cs").Removed);
        Assert.Equal(6, summary.TotalAdded);
        Assert.Equal(3, summary.TotalRemoved);
    }

    [Fact]
    public void 失败轨迹不产生改动摘要()
    {
        var trace = new[]
        {
            new ToolTraceEntry("apply_patch", ToolTraceEntry.StatusError, null, "a.cs", "未写入", 10, DateTime.Now)
        };

        Assert.True(EditSummary.FromTrace(trace).IsEmpty);
    }

    [Fact]
    public void 改动随工具轨迹JSON持久化()
    {
        var entry = Entry("a.cs", 2, 1);

        var json = JsonSerializer.Serialize(new[] { entry });
        var restored = JsonSerializer.Deserialize<ToolTraceEntry[]>(json);

        Assert.NotNull(restored);
        var edit = Assert.Single(restored![0].Edits!);
        Assert.Equal("a.cs", edit.Path);
        Assert.Equal(2, edit.Added);
    }

    private static ToolTraceEntry Entry(string path, int added, int removed) =>
        new(
            "apply_patch",
            ToolTraceEntry.StatusOk,
            null,
            path,
            "ok",
            10,
            DateTime.Now,
            [new EditedFile(path, added, removed)]);
}
