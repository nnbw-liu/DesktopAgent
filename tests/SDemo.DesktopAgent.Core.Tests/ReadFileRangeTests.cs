using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>按行读取：行号、续读提示、越界收敛、二进制与大小限制。</summary>
public sealed class ReadFileRangeTests
{
    [Fact]
    public async Task 按行读取带行号和续读提示()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.cs"), "one\ntwo\nthree\nfour\nfive\n");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file",
            ("path", "a.cs"), ("offset", 2), ("limit", 2));

        Assert.Contains("共 5 行 · 本次 2–3", result);
        Assert.Contains("2| two", result);
        Assert.Contains("3| three", result);
        Assert.Contains("接续用 offset=4", result);
        Assert.DoesNotContain("4| four", result);
    }

    [Fact]
    public async Task 偏移越界自动收敛到最后一行()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "a\nb\nc");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file",
            ("path", "a.txt"), ("offset", 99));

        Assert.Contains("共 3 行 · 本次 3–3", result);
        Assert.Contains("3| c", result);
        Assert.DoesNotContain("接续用 offset=", result);
    }

    [Fact]
    public async Task 上限超过两千行时收敛()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), string.Join('\n', Enumerable.Range(1, 2100)));
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file",
            ("path", "a.txt"), ("limit", 99999));

        Assert.Contains("本次 1–2000", result);
        Assert.Contains("接续用 offset=2001", result);
    }

    [Fact]
    public async Task 空文件返回说明()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "empty.txt"), string.Empty);
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file", ("path", "empty.txt"));

        Assert.Contains("空文件", result);
    }

    [Fact]
    public async Task 二进制文件拒绝()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Root, "bin.dat"), [0x00, 0x01, 0x02]);
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file", ("path", "bin.dat"));

        Assert.Contains("二进制", result);
    }

    [Fact]
    public async Task 超过六十四KB仍可按行读取()
    {
        using var temp = new TempDirectory();
        var lines = Enumerable.Range(1, 5000).Select(index => $"line {index} 0123456789").ToArray();
        File.WriteAllText(Path.Combine(temp.Root, "big.txt"), string.Join('\n', lines));
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file",
            ("path", "big.txt"), ("offset", 4999), ("limit", 2));

        Assert.Contains("共 5000 行 · 本次 4999–5000", result);
        Assert.Contains("5000| line 5000", result);
    }

    [Fact]
    public async Task 超过六十四KB的单行会截断但可读()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Root, "long.txt"),
            "head\n" + new string('x', 70_000) + "\ntail\n");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file",
            ("path", "long.txt"), ("offset", 2), ("limit", 1));

        Assert.Contains("共 3 行 · 本次 2–2", result);
        Assert.Contains("2| x", result);
        Assert.Contains('…', result);
        Assert.DoesNotContain("二进制", result);
    }

    [Fact]
    public async Task CRLF文件行号正确且不把回车带进内容()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "crlf.txt"), "a\r\nb\r\nc\r\n");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool.Context, tool.Pack, "read_file",
            ("path", "crlf.txt"), ("offset", 2), ("limit", 1));

        Assert.Contains("共 3 行", result);
        Assert.Contains("2| b", result);
        Assert.DoesNotContain("\r", result);
    }

    [Fact]
    public async Task 读取会登记读前写记录()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.cs"), "class A {}\n");
        var reads = new FileReadRegistry();
        var tool = Tool(temp.Root, reads);

        await InvokeAsync(tool.Context, tool.Pack, "read_file", ("path", "a.cs"));

        var record = reads.Find("session", "a.cs");
        Assert.NotNull(record);
        Assert.Equal(FileReadRegistry.HashFile(Path.Combine(temp.Root, "a.cs")), record!.Hash);
    }

    private static (FileToolPack Pack, ToolContext Context) Tool(string root, IFileReadRegistry? reads = null)
    {
        var pack = new FileToolPack();
        var context = new ToolContext(
            "session",
            root,
            [],
            new StubSettingsService(),
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            reads ?? new FileReadRegistry());
        return (pack, context);
    }

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
}
