using SDemo.DesktopAgent.Editing;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>文件改动预览（界面 diff）的算法约定：行号、上下文、截断与二进制。</summary>
public sealed class FileDiffTests
{
    [Fact]
    public void 内容一致时没有差异()
    {
        var diff = FileDiffBuilder.Build("a\nb\n", "a\nb\n");

        Assert.False(diff.HasChanges);
        Assert.Empty(diff.Lines);
    }

    [Fact]
    public void 新增和删除都带两侧行号与正负符号()
    {
        var before = "one\ntwo\nthree\n";
        var after = "one\nTWO\nthree\nfour\n";

        var diff = FileDiffBuilder.Build(before, after);

        Assert.True(diff.HasChanges);
        Assert.Equal(2, diff.Added);
        Assert.Equal(1, diff.Removed);

        var removed = diff.Lines.Single(line => line.Kind == DiffLineKind.Removed);
        var added = diff.Lines.Where(line => line.Kind == DiffLineKind.Added).ToList();

        Assert.Equal("two", removed.Text);
        Assert.Equal(2, removed.OldLine);
        Assert.Contains(added, line => line.Text == "TWO" && line.NewLine == 2);
        Assert.Contains(added, line => line.Text == "four" && line.NewLine == 4);

        // 上下文行两侧行号都有，读者才知道改动落在哪。
        var context = diff.Lines.First(line => line.Kind == DiffLineKind.Context && line.Text == "one");
        Assert.Equal(1, context.OldLine);
        Assert.Equal(1, context.NewLine);

        var text = diff.ToText();
        Assert.Contains("-two", text);
        Assert.Contains("+TWO", text);
    }

    [Fact]
    public void 新建文件全部算新增()
    {
        var diff = FileDiffBuilder.Build(string.Empty, "第一行\n第二行\n");

        Assert.Equal(2, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.All(diff.Lines.Where(line => line.Kind != DiffLineKind.Hunk), line => Assert.Equal(DiffLineKind.Added, line.Kind));
    }

    [Fact]
    public void 二进制内容不逐行比较()
    {
        var diff = FileDiffBuilder.Build("a\0b", "a\0c");

        Assert.True(diff.IsBinary);
        Assert.Empty(diff.Lines);
        Assert.Contains("二进制", diff.Note);
    }

    [Fact]
    public void 超长改动标记截断而不是卡住()
    {
        var before = string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"old-{index}"));
        var after = string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"new-{index}"));

        var diff = FileDiffBuilder.Build(before, after);

        Assert.True(diff.Truncated);
        Assert.True(diff.Lines.Count <= FileDiffBuilder.MaxOutputLines + 4);
        Assert.NotNull(diff.Note);
    }
}
