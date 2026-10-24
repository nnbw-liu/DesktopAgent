using System.IO;
using SDemo.DesktopAgent.Editing;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>界面 diff 的「改动前内容」来自 checkpoint 快照，不改磁盘、不重新扫描。</summary>
public sealed class CheckpointBeforeContentTests
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void 能读到改动前的文件内容()
    {
        var store = new CheckpointStore(Path.Combine(_temp.Root, "checkpoints"));
        var workspace = Path.Combine(_temp.Root, "workspace");
        Directory.CreateDirectory(workspace);

        var file = Path.Combine(workspace, "a.txt");
        File.WriteAllText(file, "old\n");

        store.Capture("s1", "turn-1", workspace, ["a.txt"]);
        File.WriteAllText(file, "new\n");

        Assert.True(store.TryReadBeforeContent("s1", "a.txt", out var before, out var existed));
        Assert.True(existed);
        Assert.Equal("old\n", before);
    }

    [Fact]
    public void 新建文件没有改动前内容()
    {
        var store = new CheckpointStore(Path.Combine(_temp.Root, "checkpoints"));
        var workspace = Path.Combine(_temp.Root, "workspace");
        Directory.CreateDirectory(workspace);

        store.Capture("s2", "turn-1", workspace, ["fresh.txt"]);
        File.WriteAllText(Path.Combine(workspace, "fresh.txt"), "hello\n");

        Assert.True(store.TryReadBeforeContent("s2", "fresh.txt", out var before, out var existed));
        Assert.False(existed);
        Assert.Equal(string.Empty, before);
    }

    [Fact]
    public void 没有快照时返回_false()
    {
        var store = new CheckpointStore(Path.Combine(_temp.Root, "checkpoints"));

        Assert.False(store.TryReadBeforeContent("none", "a.txt", out _, out _));
    }
}
