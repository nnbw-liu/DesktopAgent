using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>工具行由客户端从轨迹现场算卡片：形态、标题回退、状态与耗时文案。</summary>
public sealed class ToolPresentationTests
{
    [Theory]
    [InlineData("run_command", ToolCardKind.Terminal)]
    [InlineData("run_powershell", ToolCardKind.Terminal)]
    [InlineData("apply_patch", ToolCardKind.Diff)]
    [InlineData("replace_text", ToolCardKind.Diff)]
    [InlineData("read_file", ToolCardKind.File)]
    [InlineData("list_tree", ToolCardKind.File)]
    [InlineData("search_files", ToolCardKind.Search)]
    [InlineData("search_capabilities", ToolCardKind.Search)]
    [InlineData("web_search", ToolCardKind.Web)]
    [InlineData("load_skill", ToolCardKind.Skill)]
    [InlineData("brand_new_tool", ToolCardKind.Generic)]
    public void 工具类型映射到卡片形态(string toolId, ToolCardKind expected)
    {
        Assert.Equal(expected, ToolPresentation.KindOf(toolId));

        var card = ToolPresentation.Describe(Entry(toolId));
        Assert.Equal(expected, card.Kind);
    }

    [Fact]
    public void 标题按标题到参数摘要到工具名回退()
    {
        Assert.Equal("dotnet build", ToolPresentation.Describe(Entry("run_command", title: "dotnet build")).Title);
        Assert.Equal("README.md", ToolPresentation.Describe(Entry("read_file", digest: "README.md")).Title);
        Assert.Equal("read_file", ToolPresentation.Describe(Entry("read_file")).Title);
    }

    [Theory]
    [InlineData(ToolTraceEntry.StatusRunning, "运行中…")]
    [InlineData(ToolTraceEntry.StatusOk, "成功")]
    [InlineData(ToolTraceEntry.StatusError, "失败")]
    [InlineData(ToolTraceEntry.StatusRejected, "已拒绝")]
    [InlineData(ToolTraceEntry.StatusDenied, "未授权")]
    [InlineData(ToolTraceEntry.StatusCanceled, "已取消")]
    public void 状态文案保持一致(string status, string expected)
    {
        Assert.Equal(expected, ToolPresentation.StatusText(Entry("read_file", status: status)));
    }

    [Fact]
    public void 退出码非零算成功但显示码()
    {
        var entry = Entry("run_command", status: ToolTraceEntry.StatusOk) with { ExitCode = 1 };

        Assert.Equal("退出码 1", ToolPresentation.StatusText(entry));
    }

    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "0 ms")]
    [InlineData(840, "840 ms")]
    [InlineData(12400, "12.4 s")]
    public void 耗时文案(int durationMs, string expected)
    {
        Assert.Equal(expected, ToolPresentation.DurationText(Entry("run_command") with { DurationMs = durationMs }));
    }

    [Fact]
    public void 没有参数和结果时不显示展开入口()
    {
        Assert.False(ToolPresentation.Describe(Entry("read_file")).HasDetail);
        Assert.True(ToolPresentation.Describe(Entry("read_file", argsJson: "{}")).HasDetail);
        Assert.True(ToolPresentation.Describe(Entry("read_file", preview: "# README")).HasDetail);
        Assert.True(ToolPresentation.Describe(Entry("apply_patch") with
        {
            Edits = [new EditedFile("a.cs", 3, 1)]
        }).HasDetail);
    }

    [Fact]
    public void 失败判定覆盖拒绝与取消()
    {
        Assert.True(ToolPresentation.IsFailure(ToolTraceEntry.StatusError));
        Assert.True(ToolPresentation.IsFailure(ToolTraceEntry.StatusDenied));
        Assert.True(ToolPresentation.IsFailure(ToolTraceEntry.StatusRejected));
        Assert.True(ToolPresentation.IsFailure(ToolTraceEntry.StatusCanceled));
        Assert.False(ToolPresentation.IsFailure(ToolTraceEntry.StatusOk));
        Assert.False(ToolPresentation.IsFailure(ToolTraceEntry.StatusRunning));
    }

    private static ToolTraceEntry Entry(
        string toolId,
        string status = ToolTraceEntry.StatusOk,
        string? digest = null,
        string? title = null,
        string? argsJson = null,
        string? preview = null) =>
        new(
            toolId,
            status,
            null,
            digest,
            "摘要",
            10,
            DateTime.Now,
            ArgsJson: argsJson,
            Title: title,
            ResultPreview: preview);
}
