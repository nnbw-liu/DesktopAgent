using System.IO;
using SDemo.DesktopAgent.Editing;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>补丁应用：精确 / 空白容错 / 位置漂移 / 全有或全无 / hash / 换行风格。</summary>
public sealed class PatchApplierTests
{
    [Fact]
    public void 精确匹配替换一行()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\ntwo\nthree\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             one
            -two
            +TWO
             three
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(file));
        var change = Assert.Single(result.Changes);
        Assert.Equal(1, change.Added);
        Assert.Equal(1, change.Removed);
    }

    [Fact]
    public void 多hunk按内容定位且偏移正确()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\ntwo\nthree\nfour\nfive\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -one
            +ONE
            @@ -4,1 +4,1 @@
            -four
            +FOUR
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("ONE\ntwo\nthree\nFOUR\nfive\n", File.ReadAllText(file));
    }

    [Fact]
    public void 行尾空白差异可容错()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "line1\nline2   \nline3\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             line1
            -line2
            +line2 new
             line3
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("line1\nline2 new\nline3\n", File.ReadAllText(file));
    }

    [Fact]
    public void hunk头行号漂移仍能按内容找到()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "alpha\nbeta\ngamma\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -99,1 +99,1 @@
            -beta
            +BETA
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("alpha\nBETA\ngamma\n", File.ReadAllText(file));
    }

    [Fact]
    public void 内容不匹配时返回相似片段且不写入()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "alpha\nbeta\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -gamma
            +GAMMA
            """);

        Assert.False(result.Success);
        Assert.Contains("第 1 个 hunk 未匹配", result.Message);
        Assert.Contains("最近相似的片段", result.Message);
        Assert.Contains("建议先 read_file", result.Message);
        Assert.Equal("alpha\nbeta\n", File.ReadAllText(file));
    }

    [Fact]
    public void 多文件中任一失败则零写入()
    {
        using var temp = new TempDirectory();
        var first = Path.Combine(temp.Root, "a.txt");
        var second = Path.Combine(temp.Root, "b.txt");
        File.WriteAllText(first, "aaa\n");
        File.WriteAllText(second, "bbb\n");

        var result = Apply(temp.Root, """
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
            """);

        Assert.False(result.Success);
        Assert.Equal("aaa\n", File.ReadAllText(first));
        Assert.Equal("bbb\n", File.ReadAllText(second));
    }

    [Fact]
    public void expectedHash不匹配时拒绝()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "one\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -one
            +ONE
            """, expectedHash: "deadbeef");

        Assert.False(result.Success);
        Assert.Contains("hash 不匹配", result.Message);
        Assert.Equal("one\n", File.ReadAllText(file));
    }

    [Fact]
    public void 新文件补丁会创建父目录()
    {
        using var temp = new TempDirectory();

        var result = Apply(temp.Root, """
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,2 @@
            +class New
            +{
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("class New\n{\n", File.ReadAllText(Path.Combine(temp.Root, "src", "New.cs")));
        Assert.True(Assert.Single(result.Changes).Created);
    }

    [Fact]
    public void 保留CRLF换行风格()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "a\r\nb\r\n");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             a
            -b
            +c
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("a\r\nc\r\n", File.ReadAllText(file));
    }

    [Fact]
    public void 保留无末尾换行状态()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "a\nb");

        var result = Apply(temp.Root, """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
            -a
            +A
             b
            \ No newline at end of file
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("A\nb", File.ReadAllText(file));
    }

    [Fact]
    public void 无末尾换行标记作用于新文件()
    {
        using var temp = new TempDirectory();

        var result = Apply(temp.Root, """
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,2 @@
            +x
            +y
            \ No newline at end of file
            """);

        Assert.True(result.Success, result.Message);
        Assert.Equal("x\ny", File.ReadAllText(Path.Combine(temp.Root, "new.txt")));
    }

    private static PatchResult Apply(string root, string patch, string? expectedHash = null)
    {
        var parsed = UnifiedPatchParser.Parse(patch);
        return new PatchApplier().Apply(root, parsed, expectedHash);
    }
}
