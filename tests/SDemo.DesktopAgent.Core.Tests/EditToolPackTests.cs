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

    [Fact]
    public async Task replace_text未读文件时拒绝()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "one\n");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "replace_text", ("path", "a.txt"), ("oldText", "one"), ("newText", "ONE"));

        Assert.Contains("先 read_file", result);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(temp.Root, "a.txt")));
    }

    [Fact]
    public async Task replace_text读后精确替换()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\ntwo\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "replace_text", ("path", "a.txt"), ("oldText", "two"), ("newText", "TWO"));

        Assert.Contains("已修改 1 个文件", result);
        Assert.Equal("one\nTWO\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text按LF匹配CRLF文件并保留换行()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\r\ntwo\r\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "replace_text", ("path", "a.txt"), ("oldText", "one\ntwo"), ("newText", "ONE\nTWO"));

        Assert.Contains("已修改 1 个文件", result);
        Assert.Equal("ONE\r\nTWO\r\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text找不到原文时不写文件()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "replace_text", ("path", "a.txt"), ("oldText", "missing"), ("newText", "x"));

        Assert.Contains("找不到 old_text", result);
        Assert.Equal("one\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text的读记录忽略点斜杠前缀()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "./a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "replace_text", ("path", "a.txt"), ("oldText", "one"), ("newText", "ONE"));

        Assert.Contains("已修改 1 个文件", result);
        Assert.Equal("ONE\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text拒绝重复插入已有内容()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\nTWO\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context,
            tool.Pack,
            "replace_text",
            ("path", "a.txt"),
            ("oldText", "one"),
            ("newText", "one\nTWO"));

        Assert.Contains("疑似重复插入", result);
        Assert.Equal("one\nTWO\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text缩进不一致时按Trim匹配()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "line\n    }\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "replace_text", ("path", "a.txt"), ("oldText", "line\n}"), ("newText", "LINE\n}"));

        Assert.Contains("已修改 1 个文件", result);
        Assert.Contains("Trim", result);
        Assert.Equal("LINE\n}\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text找不到时给出最接近的行()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "alpha\nbeta\ngamma\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context,
            tool.Pack,
            "replace_text",
            ("path", "a.txt"),
            ("oldText", "beta\nextra"),
            ("newText", "BETA"));

        Assert.Contains("最接近的位置在第 2 行", result);
        Assert.Contains("2| beta", result);
        Assert.Equal("alpha\nbeta\ngamma\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task write_file拒绝明显缩水的已有文件覆盖()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        var original = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i} with enough content")) + "\n";
        File.WriteAllText(file, original);
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context, tool.Pack, "write_file", ("path", "a.txt"), ("content", "short"));

        Assert.Contains("疑似整文件重写丢失内容", result);
        Assert.Equal(original, File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text拒绝重复添加已有成员()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "/// old\npublic bool X { get; set; } = true;\n\nmiddle\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context,
            tool.Pack,
            "replace_text",
            ("path", "a.txt"),
            ("oldText", "middle"),
            ("newText", "/// new\npublic bool X { get; set; } = true;\nmiddle"));

        Assert.Contains("疑似重复添加", result);
        Assert.Equal("/// old\npublic bool X { get; set; } = true;\n\nmiddle\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task replace_text允许复用相同的语句行()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "File.WriteAllText(path, \"{}\");\n\nmiddle\n");
        var tool = Tool(temp.Root);
        tool.Reads.RecordRead("session", "a.txt", FileReadRegistry.HashFile(file)!);

        var result = await InvokeAsync(
            tool.Context,
            tool.Pack,
            "replace_text",
            ("path", "a.txt"),
            ("oldText", "middle"),
            ("newText", "File.WriteAllText(path, \"{}\");\nmiddle"));

        Assert.DoesNotContain("疑似重复添加", result);
        Assert.Contains("已修改 1 个文件", result);
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
