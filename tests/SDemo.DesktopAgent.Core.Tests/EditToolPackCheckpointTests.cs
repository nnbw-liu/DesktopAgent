using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>编辑前自动 checkpoint：补丁成功后能恢复原文件。</summary>
public sealed class EditToolPackCheckpointTests
{
    [Fact]
    public async Task 补丁成功后撤销恢复原内容()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "old\n");
        var checkpoints = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));
        var settings = new StubSettingsService();
        settings.Current.Checkpoints.Enabled = true;
        settings.Current.ConfirmFileWrite = false;
        var reads = new FileReadRegistry();
        reads.RecordRead("s1", "a.txt", FileReadRegistry.HashFile(file)!);
        var context = new ToolContext(
            "s1",
            temp.Root,
            [],
            settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            reads,
            Session: ChatSession.CreateEmpty(),
            TurnId: "turn-1");
        var pack = new EditToolPack(checkpoints);

        var result = await InvokeAsync(pack, context, "apply_patch", ("patch", """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            +new
            """));

        Assert.Contains("已修改", result);
        Assert.Equal("new\n", File.ReadAllText(file));
        var checkpoint = checkpoints.Latest("s1");
        Assert.NotNull(checkpoint);

        var restored = checkpoints.RestoreLatest("s1", temp.Root);

        Assert.True(restored.Success);
        Assert.Equal("old\n", File.ReadAllText(file));
    }

    private static async Task<string> InvokeAsync(
        IAgentToolPack pack,
        ToolContext context,
        string name,
        params (string Key, object? Value)[] arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            pack.Create(context).Single(function => function.Name == name));
        var values = arguments.ToDictionary(pair => pair.Key, pair => pair.Value);
        return (await tool.InvokeAsync(new AIFunctionArguments(values)))?.ToString() ?? string.Empty;
    }
}
