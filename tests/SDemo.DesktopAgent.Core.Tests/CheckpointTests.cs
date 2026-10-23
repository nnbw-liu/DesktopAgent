using System.IO;
using SDemo.DesktopAgent.Editing;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P5 checkpoint：多文件备份、同 turn 合并、冲突、新建文件撤销、保留上限。</summary>
public sealed class CheckpointTests
{
    [Fact]
    public void 多文件备份与恢复()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        var first = Path.Combine(workspace, "a.txt");
        var second = Path.Combine(workspace, "b.txt");
        File.WriteAllText(first, "old-a");
        File.WriteAllText(second, "old-b");
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));

        store.Capture("s1", "turn-1", workspace, ["a.txt", "b.txt"]);
        File.WriteAllText(first, "new-a");
        File.WriteAllText(second, "new-b");

        var result = store.RestoreLatest("s1", workspace);

        Assert.True(result.Success);
        Assert.Equal("old-a", File.ReadAllText(first));
        Assert.Equal("old-b", File.ReadAllText(second));
        Assert.Equal(2, result.Restored.Count);
    }

    [Fact]
    public void 同turn多次编辑只保留一个检查点()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "a");
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));

        store.Capture("s1", "turn-1", workspace, ["a.txt"]);
        store.Capture("s1", "turn-1", workspace, ["a.txt"]);

        Assert.Single(store.List("s1"));
    }

    [Fact]
    public void 外部改动冲突时不覆盖()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        var file = Path.Combine(workspace, "a.txt");
        File.WriteAllText(file, "old");
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));

        store.Capture("s1", "turn-1", workspace, ["a.txt"]);
        File.WriteAllText(file, "model");
        store.MarkEdited("s1", "turn-1", workspace, ["a.txt"]);
        File.WriteAllText(file, "external");

        var result = store.RestoreLatest("s1", workspace);

        Assert.False(result.Success);
        Assert.Contains("a.txt", result.Conflicts);
        Assert.Equal("external", File.ReadAllText(file));
    }

    [Fact]
    public void 强制恢复覆盖冲突()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        var file = Path.Combine(workspace, "a.txt");
        File.WriteAllText(file, "old");
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));
        store.Capture("s1", "turn-1", workspace, ["a.txt"]);
        File.WriteAllText(file, "model");
        store.MarkEdited("s1", "turn-1", workspace, ["a.txt"]);
        File.WriteAllText(file, "external");

        var result = store.RestoreLatest("s1", workspace, force: true);

        Assert.True(result.Success);
        Assert.Equal("old", File.ReadAllText(file));
    }

    [Fact]
    public void 新建文件撤销时移入回收站()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));
        store.Capture("s1", "turn-1", workspace, ["new.txt"]);
        File.WriteAllText(Path.Combine(workspace, "new.txt"), "created");

        var result = store.RestoreLatest("s1", workspace);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(workspace, "new.txt")));
    }

    [Fact]
    public void 只保留最近二十个检查点()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        var file = Path.Combine(workspace, "a.txt");
        File.WriteAllText(file, "a");
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));

        for (var index = 0; index < CheckpointStore.KeepPerSession + 3; index++)
        {
            store.Capture("s1", $"turn-{index}", workspace, ["a.txt"]);
            Thread.Sleep(2);
        }

        Assert.Equal(CheckpointStore.KeepPerSession, store.List("s1").Count);
    }

    [Fact]
    public void 删除会话清理检查点()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "a");
        var store = new CheckpointStore(Path.Combine(temp.Root, "checkpoints"));
        store.Capture("s1", "turn-1", workspace, ["a.txt"]);

        store.DeleteSession("s1");

        Assert.Empty(store.List("s1"));
    }
}
