using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>工具轨迹（§3.6）：紧凑格式、预算回放、生成端摘要与参数脱敏。</summary>
public sealed class ToolTraceTests
{
    private readonly HeuristicTokenCounter _counter = new();

    private static ToolTraceEntry Entry(
        string tool = "read_file",
        string status = ToolTraceEntry.StatusOk,
        int? exit = null,
        string? digest = "docs/a.md",
        string? summary = "读取成功") => new(tool, status, exit, digest, summary, 1234, DateTime.Now);

    [Fact]
    public void 轨迹行包含工具_状态_退出码与摘要()
    {
        var line = ToolTraceFormatter.RenderLine(Entry("powershell", ToolTraceEntry.StatusError, 1, "scripts/run.ps1", "命令失败"));

        Assert.Contains("[工具] powershell", line);
        Assert.Contains("scripts/run.ps1", line);
        Assert.Contains("error", line);
        Assert.Contains("exit 1", line);
        Assert.Contains("命令失败", line);
    }

    [Fact]
    public void 没有摘要时回放输出长度()
    {
        var line = ToolTraceFormatter.RenderLine(Entry(summary: null));

        Assert.Contains("1.2K 字符", line);
    }

    [Fact]
    public void 回放按预算从新到旧并说明省略()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(index => Entry(summary: $"第 {index} 次调用成功，返回了一段不算长的说明"))
            .ToList();

        var render = ToolTraceFormatter.Render(entries, tokenBudget: 40, _counter);

        Assert.NotNull(render);
        Assert.True(render!.Tokens <= 40);
        Assert.True(render.Omitted > 0);
        Assert.Contains("已省略", render.Text);
    }

    [Fact]
    public void 预算为零时不回放轨迹()
    {
        Assert.Null(ToolTraceFormatter.Render([Entry()], tokenBudget: 0, _counter));
        Assert.Null(ToolTraceFormatter.Render([], tokenBudget: 100, _counter));
    }

    [Fact]
    public void 摘要截断到上限以内()
    {
        var text = new string('x', 5000) + "尾部标记";

        var summary = AgentChatService.Summarize(text);

        Assert.NotNull(summary);
        Assert.True(summary!.Length <= ToolTraceFormatter.MaxSummaryChars + 8);
        Assert.Contains("尾部标记", summary);
    }

    [Fact]
    public void 参数摘要优先保留路径_脚本正文只留hash()
    {
        var path = AgentChatService.DigestArguments(new Dictionary<string, object?> { ["path"] = "docs/report.md" });
        var script = AgentChatService.DigestArguments(new Dictionary<string, object?>
        {
            ["script"] = new string('x', 10_000)
        });

        Assert.Equal("docs/report.md", path);
        Assert.NotNull(script);
        Assert.StartsWith("sha256:", script);
        Assert.DoesNotContain(new string('x', 50), script);
    }

    [Fact]
    public void 状态分类识别退出码与用户拒绝()
    {
        var failed = AgentChatService.Classify("执行失败：exit code 1");
        Assert.Equal(ToolTraceEntry.StatusError, failed.Status);
        Assert.Equal(1, failed.ExitCode);

        var denied = AgentChatService.Classify("用户拒绝了这次工具调用");
        Assert.Equal(ToolTraceEntry.StatusDenied, denied.Status);

        var ok = AgentChatService.Classify("读取完成，共 12 行");
        Assert.Equal(ToolTraceEntry.StatusOk, ok.Status);

        var empty = AgentChatService.Classify(null);
        Assert.Equal(ToolTraceEntry.StatusError, empty.Status);
    }

    [Fact]
    public void 工具状态标记优先于文本猜测()
    {
        var rejected = AgentChatService.Classify(
            "[TOOL_RESULT_BEGIN:write_file]\n[TOOL_STATUS:rejected]\n未写入 a.cs：疑似整文件重写丢失内容\n[TOOL_RESULT_END]");
        Assert.Equal(ToolTraceEntry.StatusRejected, rejected.Status);

        var failed = AgentChatService.Classify(
            "[TOOL_RESULT_BEGIN:apply_patch]\n[TOOL_STATUS:error]\n工具执行失败（未修改文件）\n[TOOL_RESULT_END]");
        Assert.Equal(ToolTraceEntry.StatusError, failed.Status);

        // 没有标记时仍走旧启发式，兼容历史消息 / 旧库。
        var legacy = AgentChatService.Classify(
            "[TOOL_RESULT_BEGIN:apply_patch]\n未写入任何文件：第 1 个 hunk 未匹配\n[TOOL_RESULT_END]");
        Assert.Equal(ToolTraceEntry.StatusRejected, legacy.Status);
    }
}
