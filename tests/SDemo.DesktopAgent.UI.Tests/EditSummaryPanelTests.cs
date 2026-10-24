using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 「本次改动」面板：点一个文件就展开 diff，并提供打开 / 定位。
/// 数据只来自工具轨迹和 checkpoint 快照，不额外扫盘。
/// </summary>
public sealed class EditSummaryPanelTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly string _workspace;

    public EditSummaryPanelTests()
    {
        _workspace = Path.Combine(_temp.Root, "workspace");
        Directory.CreateDirectory(_workspace);

        var paths = new StubAppPaths(_temp.Root);
        var workspace = new WorkspaceContext(paths) { RootPath = _workspace };
        var checkpoints = new CheckpointStore(Path.Combine(paths.DataRoot, "checkpoints"), new NoAudit());

        Services.AddSingleton(checkpoints);
        Services.AddSingleton(workspace);
        Services.AddSingleton<IFileLauncherService>(new TrackingLauncher());
    }

    [Fact]
    public void 点开文件行后显示_diff_并按行着色()
    {
        var file = Path.Combine(_workspace, "src", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "line1\nline2\n");

        var checkpoints = Services.GetRequiredService<CheckpointStore>();
        checkpoints.Capture("s1", "turn-1", _workspace, ["src/a.cs"]);
        File.WriteAllText(file, "line1\nchanged\nline3\n");

        var trace = new[]
        {
            new ToolTraceEntry(
                "apply_patch",
                ToolTraceEntry.StatusOk,
                null,
                "src/a.cs",
                "已写入 1 处",
                30,
                DateTime.Now,
                [new EditedFile("src/a.cs", 2, 1)])
        };

        var cut = RenderComponent<EditSummaryPanel>(parameters => parameters
            .Add(component => component.Trace, trace)
            .Add(component => component.SessionId, "s1"));

        cut.Find(".edit-summary-open").Click();

        Assert.Equal(2, cut.FindAll(".edit-diff-line.is-added").Count);
        Assert.Single(cut.FindAll(".edit-diff-line.is-removed"));
        Assert.Contains("changed", cut.Find(".edit-diff").TextContent, StringComparison.Ordinal);
        Assert.Contains("+2", cut.Find(".edit-summary-counts").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有快照时给出解释而不是空的_diff()
    {
        var file = Path.Combine(_workspace, "b.txt");
        File.WriteAllText(file, "hello\n");

        var trace = new[]
        {
            new ToolTraceEntry(
                "write_file",
                ToolTraceEntry.StatusOk,
                null,
                "b.txt",
                "已写入",
                10,
                DateTime.Now,
                [new EditedFile("b.txt", 1, 0)])
        };

        var cut = RenderComponent<EditSummaryPanel>(parameters => parameters
            .Add(component => component.Trace, trace)
            .Add(component => component.SessionId, "no-such-session"));

        cut.Find(".edit-summary-open").Click();

        // 没有 checkpoint 就按「全部新增」展示，至少能快速看一眼这次写了什么。
        Assert.Single(cut.FindAll(".edit-diff-line.is-added"));
    }

    [Fact]
    public async Task 打开文件走宿主实现()
    {
        var file = Path.Combine(_workspace, "c.txt");
        File.WriteAllText(file, "x\n");

        var trace = new[]
        {
            new ToolTraceEntry(
                "write_file",
                ToolTraceEntry.StatusOk,
                null,
                "c.txt",
                "已写入",
                4,
                DateTime.Now,
                [new EditedFile("c.txt", 1, 0)])
        };

        var launcher = (TrackingLauncher)Services.GetRequiredService<IFileLauncherService>();

        var cut = RenderComponent<EditSummaryPanel>(parameters => parameters
            .Add(component => component.Trace, trace)
            .Add(component => component.SessionId, "s2"));

        await cut.InvokeAsync(() => cut.FindAll(".ghost-button--tiny").First().Click());

        Assert.Equal(file, launcher.Opened);
    }

    private sealed class TrackingLauncher : IFileLauncherService
    {
        public string? Opened { get; private set; }

        public string? Revealed { get; private set; }

        public Task<bool> OpenFileAsync(string path)
        {
            Opened = path;
            return Task.FromResult(true);
        }

        public Task<bool> RevealFileAsync(string path)
        {
            Revealed = path;
            return Task.FromResult(true);
        }
    }
}
