using SDemo.DesktopAgent.Editing;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>unified diff 解析：单/多文件、多 hunk、CRLF、新文件与路径安全。</summary>
public sealed class PatchParserTests
{
    [Fact]
    public void 解析单文件单hunk()
    {
        var patch = """
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -2,3 +2,4 @@
             class A
            -{
            +{
            +    // cache
             }
            """;

        var parsed = UnifiedPatchParser.Parse(patch);

        var file = Assert.Single(parsed.Files);
        Assert.Equal("src/A.cs", file.OldPath);
        Assert.Equal("src/A.cs", file.NewPath);
        Assert.False(file.IsNew);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(2, hunk.OldStart);
        Assert.Equal(3, hunk.OldCount);
        Assert.Equal(4, hunk.NewCount);
        Assert.Equal(5, hunk.Lines.Count);
    }

    [Fact]
    public void 解析多文件多hunk()
    {
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -one
            +ONE
            @@ -10,1 +10,1 @@
            -ten
            +TEN
            --- a/b.txt
            +++ b/b.txt
            @@ -1,1 +1,1 @@
            -bee
            +BEE
            """;

        var parsed = UnifiedPatchParser.Parse(patch);

        Assert.Equal(2, parsed.Files.Count);
        Assert.Equal(2, parsed.Files[0].Hunks.Count);
        Assert.Single(parsed.Files[1].Hunks);
    }

    [Fact]
    public void 解析CRLF补丁()
    {
        var patch = "--- a/a.txt\r\n+++ b/a.txt\r\n@@ -1,1 +1,1 @@\r\n-old\r\n+new\r\n";

        var parsed = UnifiedPatchParser.Parse(patch);

        Assert.Equal("new", parsed.Files[0].Hunks[0].Lines[1].Text);
    }

    [Fact]
    public void 解析无末尾换行标记()
    {
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
            """;

        var parsed = UnifiedPatchParser.Parse(patch);

        var hunk = Assert.Single(parsed.Files[0].Hunks);
        Assert.True(hunk.Lines[0].NoNewline);
        Assert.True(hunk.Lines[1].NoNewline);
    }

    [Fact]
    public void 新文件支持devnull旧路径()
    {
        var patch = """
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,2 @@
            +class New
            +{
            """;

        var parsed = UnifiedPatchParser.Parse(patch);

        var file = Assert.Single(parsed.Files);
        Assert.True(file.IsNew);
        Assert.Null(file.OldPath);
        Assert.Equal("src/New.cs", file.NewPath);
    }

    [Fact]
    public void 删除文件本周期拒绝()
    {
        var patch = """
            --- a/a.txt
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -old
            """;

        var error = Assert.Throws<InvalidOperationException>(() => UnifiedPatchParser.Parse(patch));
        Assert.Contains("不支持删除", error.Message);
    }

    [Theory]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("../../outside.txt")]
    [InlineData("//server/share/a.txt")]
    public void 非法路径拒绝(string path)
    {
        var patch = $"--- a/a.txt\n+++ b/{path}\n@@ -1,1 +1,1 @@\n-old\n+new\n";

        var error = Assert.Throws<InvalidOperationException>(() => UnifiedPatchParser.Parse(patch));
        Assert.True(error.Message.Contains("拒绝") || error.Message.Contains("不支持"));
    }

    [Fact]
    public void 接受Copilot开始结束标记()
    {
        var parsed = UnifiedPatchParser.Parse("""
            *** Begin Patch
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            +new
            *** End Patch
            """);

        var file = Assert.Single(parsed.Files);
        Assert.Equal("a.txt", file.NewPath);
    }

    [Fact]
    public void 缺少文件头时报错()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UnifiedPatchParser.Parse("hello"));
        Assert.Contains("不是补丁头", error.Message);
    }

    [Fact]
    public void 空hunk报错()
    {
        var patch = "--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n";

        var error = Assert.Throws<InvalidOperationException>(() => UnifiedPatchParser.Parse(patch));
        Assert.Contains("没有内容", error.Message);
    }

    [Fact]
    public void 重复文件报错()
    {
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -a
            +b
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -b
            +c
            """;

        var error = Assert.Throws<InvalidOperationException>(() => UnifiedPatchParser.Parse(patch));
        Assert.Contains("重复", error.Message);
    }

    [Fact]
    public void 文件名带时间戳仍能解析()
    {
        var patch = "--- a/a.txt\t2024-01-01 00:00:00\n+++ b/a.txt\t2024-01-01 00:00:00\n@@ -1,1 +1,1 @@\n-a\n+b\n";

        var parsed = UnifiedPatchParser.Parse(patch);

        Assert.Equal("a.txt", parsed.Files[0].NewPath);
    }
}
