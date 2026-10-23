using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P6 单工具：read / search / patch 与现有工具一致。</summary>
public sealed class CodeEditTests
{
    [Fact]
    public async Task 读取动作()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "hello");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool, "read", path: "a.txt");

        Assert.Contains("hello", result);
        Assert.Contains("1| hello", result);
    }

    [Fact]
    public async Task 搜索动作()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.cs"), "class Alpha {}\n");
        var tool = Tool(temp.Root);

        var result = await InvokeAsync(tool, "search", query: "Alpha", path: "**/*.cs");

        Assert.Contains("a.cs", result);
        Assert.Contains("Alpha", result);
    }

    [Fact]
    public async Task 补丁动作仍然要求先读()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "old\n");
        var context = Context(temp.Root);
        var tool = new CodeEditToolPack().Create(context).OfType<AIFunction>()
            .First(function => function.Name == "code_edit");

        var blocked = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["action"] = "patch",
            ["patch"] = "--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n"
        }));

        Assert.Contains("先 read_file", blocked?.ToString());

        context.Reads!.RecordRead("s1", "a.txt", FileReadRegistry.HashFile(file)!);
        var applied = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["action"] = "patch",
            ["patch"] = "--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n"
        }));

        Assert.Contains("已修改", applied?.ToString());
        Assert.Equal("new\n", File.ReadAllText(file));
    }

    private static AIFunction Tool(string root)
    {
        var context = Context(root);
        return new CodeEditToolPack().Create(context).OfType<AIFunction>()
            .First(function => function.Name == "code_edit");
    }

    private static ToolContext Context(string root)
    {
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = false;
        return new ToolContext(
            "s1",
            root,
            [],
            settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            new FileReadRegistry());
    }

    private static async Task<string> InvokeAsync(
        AIFunction tool,
        string action,
        string? path = null,
        string? query = null)
    {
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["path"] = path,
            ["query"] = query
        }));

        return result?.ToString() ?? string.Empty;
    }
}
