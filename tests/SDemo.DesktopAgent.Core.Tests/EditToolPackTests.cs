using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>写入工具：读前写、审批 diff、全有或全无、hash 变化。</summary>
public sealed class EditToolPackTests
{
    [Fact]
    public async Task 未读已有文件时apply_patch拒绝()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "one\n");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", Patch()));

        Assert.Contains("先 read_file", result);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(temp.Root, "a.txt")));
    }

    [Fact]
    public async Task 读后apply_patch成功并登记新hash()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", Patch()));

        Assert.Contains("已修改 1 个文件", result);
        Assert.Contains("[EDIT] a.txt +1 -1", result);
        Assert.Equal("ONE\n", File.ReadAllText(file));
        Assert.Equal(FileReadRegistry.HashFile(file), tool.Reads.Find("session", "a.txt")!.Hash);
    }

    [Fact]
    public async Task 文件被外部改动后拒绝()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        File.WriteAllText(file, "one changed\n");

        var result = await InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", Patch()));

        Assert.Contains("文件已被改动", result);
        Assert.Equal("one changed\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task 多文件补丁任一失败则零写入()
    {
        using var temp = new TempDirectory();
        var a = Path.Combine(temp.Root, "a.txt");
        var b = Path.Combine(temp.Root, "b.txt");
        File.WriteAllText(a, "aaa\n");
        File.WriteAllText(b, "bbb\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(a)!);
        tool.Reads.RecordRead("session", "b.txt", FileReadRegistry.HashFile(b)!);

        var result = await InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -aaa
            +AAA
            --- a/b.txt
            +++ b/b.txt
            @@ -1,1 +1,1 @@
            -nope
            +NOPE
            """));

        Assert.Contains("未写入任何文件", result);
        Assert.Equal("aaa\n", File.ReadAllText(a));
        Assert.Equal("bbb\n", File.ReadAllText(b));
    }

    [Fact]
    public async Task 审批展示diff且拒绝后不落盘()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\n");
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = true;
        var approvals = new ToolApprovalService();
        var tool = Tool(temp.Root, settings, approvals);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var invocation = InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", Patch()));
        await WaitUntilAsync(() => approvals.Current is not null);

        Assert.Equal(ToolApprovalKind.FileEdit, approvals.Current!.Kind);
        Assert.False(approvals.Current.AllowSessionRemember);
        Assert.Contains("@@", approvals.Current.Detail);
        Assert.Contains("-one", approvals.Current.Detail);
        Assert.Contains("+ONE", approvals.Current.Detail);

        approvals.Resolve(ToolApprovalDecision.Deny);
        var result = await invocation;

        Assert.Contains("拒绝了", result);
        Assert.Equal("one\n", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("*** Begin Patch\n*** End Patch")]
    [InlineData("--- a/a.txt\n+++ b/a.txt\n@@\n-one\n+ONE\n")]
    [InlineData("*** Update File: a.txt\n@@ -1,1 +1,1 @@\n-one\n+ONE\n")]
    public async Task 三种坏补丁都返回标准格式提示(string patch)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "one\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(Path.Combine(temp.Root, "a.txt"))!);

        var result = await InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", patch));

        Assert.Contains("未写入任何文件", result);
        Assert.Contains("标准 unified diff", result);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(temp.Root, "a.txt")));
    }

    [Fact]
    public async Task 新建文件不需要先读()
    {
        using var temp = new TempDirectory();
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "apply_patch", ("patch", """
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,1 @@
            +hello
            """));

        Assert.Contains("已修改 1 个文件", result);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(temp.Root, "new.txt")));
    }

    private static (EditToolPack Pack, ToolContext Context, FileReadRegistry Reads) Tool(
        string root,
        StubSettingsService? settings = null,
        IToolApprovalService? approvals = null)
    {
        var reads = new FileReadRegistry();
        if (settings is null)
        {
            settings = new StubSettingsService();
            settings.Current.ConfirmFileWrite = false;
        }
        var context = new ToolContext(
            "session",
            root,
            [],
            settings,
            approvals ?? new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            reads);
        return (new EditToolPack(), context, reads);
    }

    private static string Patch() => """
        --- a/a.txt
        +++ b/a.txt
        @@ -1,1 +1,1 @@
        -one
        +ONE
        """;

    private static async Task<string> InvokeAsync(
        ToolContext context,
        IAgentToolPack pack,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            pack.Create(context).Single(function => function.Name == toolName));
        var values = arguments.ToDictionary(pair => pair.Key, pair => pair.Value);
        return (await tool.InvokeAsync(new AIFunctionArguments(values)))?.ToString() ?? string.Empty;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("等待审批请求超时。");
    }
}
