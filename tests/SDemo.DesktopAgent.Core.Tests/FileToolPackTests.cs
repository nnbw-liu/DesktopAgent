using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>文件工具包：路径 jail、写入审批、原子覆盖。</summary>
public sealed class FileToolPackTests
{
    [Fact]
    public async Task 读取与列出工作区文件()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "hello");
        var context = Context(temp.Root, new StubSettingsService(), new ToolApprovalService());
        var pack = new FileToolPack();

        var read = await InvokeAsync(pack, context, "read_file", ("path", "a.txt"));
        var list = await InvokeAsync(pack, context, "list_files", ("path", "."));

        Assert.Equal("hello", read);
        Assert.Contains("[文件] a.txt", list);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\file.txt")]
    public async Task 拒绝越界路径(string path)
    {
        using var temp = new TempDirectory();
        var context = Context(temp.Root, new StubSettingsService(), new ToolApprovalService());
        var pack = new FileToolPack();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(pack, context, "read_file", ("path", path)));
    }

    [Fact]
    public async Task 拒绝符号链接()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var link = Path.Combine(temp.Root, "link");
        File.WriteAllText(Path.Combine(outside.Root, "secret.txt"), "secret");

        try
        {
            Directory.CreateSymbolicLink(link, outside.Root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 标准用户默认没有创建符号链接的权限：跳过用例，不把环境限制当成产品缺陷。
            return;
        }

        var context = Context(temp.Root, new StubSettingsService(), new ToolApprovalService());
        var pack = new FileToolPack();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(pack, context, "read_file", ("path", "link/secret.txt")));
    }

    [Fact]
    public async Task 写入被拒绝时文件不落盘()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = true;
        var approvals = new ToolApprovalService();
        var context = Context(temp.Root, settings, approvals);
        var pack = new FileToolPack();

        var invocation = InvokeAsync(pack, context, "write_file", ("path", "new.txt"), ("content", "data"));

        await WaitUntilAsync(() => approvals.Current is not null);
        approvals.Resolve(ToolApprovalDecision.Deny);

        var result = await invocation;
        Assert.Contains("拒绝了", result);
        Assert.False(File.Exists(Path.Combine(temp.Root, "new.txt")));
    }

    [Fact]
    public async Task 批准后写入并可覆盖同名文件()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Root, "sub", "new.txt");
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = true;
        var approvals = new ToolApprovalService();
        var context = Context(temp.Root, settings, approvals);
        var pack = new FileToolPack();

        var invocation = InvokeAsync(pack, context, "write_file", ("path", "sub/new.txt"), ("content", "第一次"));
        await WaitUntilAsync(() => approvals.Current is not null);
        Assert.Equal("sub/new.txt", approvals.Current!.Summary.Replace("路径：", string.Empty));
        Assert.Equal("第一次", approvals.Current.Detail);
        approvals.Resolve(ToolApprovalDecision.ApproveOnce);
        await invocation;

        Assert.Equal("第一次", File.ReadAllText(target));

        var overwrite = InvokeAsync(pack, context, "write_file", ("path", "sub/new.txt"), ("content", "第二次"));
        await WaitUntilAsync(() => approvals.Current is not null);
        approvals.Resolve(ToolApprovalDecision.ApproveOnce);
        await overwrite;

        Assert.Equal("第二次", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(temp.Root, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 关闭写入确认时不弹审批()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = false;
        var approvals = new ToolApprovalService();
        var context = Context(temp.Root, settings, approvals);
        var pack = new FileToolPack();

        var result = await InvokeAsync(pack, context, "write_file", ("path", "a.txt"), ("content", "x"));

        Assert.Contains("已写入", result);
        Assert.Equal("x", File.ReadAllText(Path.Combine(temp.Root, "a.txt")));
        Assert.Null(approvals.Current);
    }

    private static ToolContext Context(string root, StubSettingsService settings, IToolApprovalService approvals) =>
        new("session", root, [], settings, approvals, new StubSandboxJobService(), new NullAuditLog());

    private static async Task<string> InvokeAsync(
        IAgentToolPack pack,
        ToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            pack.Create(context).Single(function => function.Name == toolName));

        var values = arguments.ToDictionary(pair => pair.Key, pair => pair.Value);
        var result = await tool.InvokeAsync(new AIFunctionArguments(values));
        return result?.ToString() ?? string.Empty;
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
