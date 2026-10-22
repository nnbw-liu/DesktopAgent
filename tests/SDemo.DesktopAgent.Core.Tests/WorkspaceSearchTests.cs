using System.IO;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>搜索 / glob / 目录树：glob 语义、排除目录、二进制、上限与无结果文案。</summary>
public sealed class WorkspaceSearchTests
{
    [Fact]
    public void 正则命中输出相对路径和行号()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src"));
        File.WriteAllText(Path.Combine(temp.Root, "src", "A.cs"), "namespace X;\nclass A {}\n");
        File.WriteAllText(Path.Combine(temp.Root, "notes.txt"), "class not code\n");
        var search = new WorkspaceSearch();

        var result = search.Search(temp.Root, "class", "**/*.cs");

        Assert.Contains("src/A.cs:2: class A {}", result.Text);
        Assert.DoesNotContain("notes.txt", result.Text);
        Assert.Equal(["src/A.cs"], result.MatchedPaths);
    }

    [Fact]
    public void glob限定只搜索匹配文件()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.md"), "target");
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "target");
        var search = new WorkspaceSearch();

        var result = search.Search(temp.Root, "target", "**/*.md");

        Assert.Contains("a.md:1:", result.Text);
        Assert.DoesNotContain("a.txt", result.Text);
    }

    [Fact]
    public void 默认不区分大小写且可切换()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "Hello");
        var search = new WorkspaceSearch();

        Assert.Contains("a.txt:1:", search.Search(temp.Root, "hello").Text);
        Assert.Contains("未找到", search.Search(temp.Root, "hello", caseSensitive: true).Text);
    }

    [Fact]
    public void 排除构建目录和版本库()
    {
        using var temp = new TempDirectory();
        foreach (var directory in new[] { "bin", "obj", "node_modules", ".git", ".vs", "TestResults" })
        {
            Directory.CreateDirectory(Path.Combine(temp.Root, directory));
            File.WriteAllText(Path.Combine(temp.Root, directory, "x.txt"), "needle");
        }

        var result = new WorkspaceSearch().Search(temp.Root, "needle");

        Assert.Contains("未找到", result.Text);
    }

    [Fact]
    public void 无结果时给出放宽建议()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "abc");

        var result = new WorkspaceSearch().Search(temp.Root, "zzz");

        Assert.Contains("未找到", result.Text);
        Assert.Contains("放宽 glob", result.Text);
    }

    [Fact]
    public void 非法正则返回可读错误()
    {
        using var temp = new TempDirectory();

        var result = new WorkspaceSearch().Search(temp.Root, "[");

        Assert.Contains("正则无效", result.Text);
    }

    [Fact]
    public void 二进制文件不参与搜索()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Root, "bin.dat"), [0x6E, 0x65, 0x00, 0x65]);
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "needle");

        var result = new WorkspaceSearch().Search(temp.Root, "needle");

        Assert.Contains("a.txt:1:", result.Text);
        Assert.DoesNotContain("bin.dat", result.Text);
    }

    [Fact]
    public void 超大文件跳过()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "huge.txt"), new string('x', 600 * 1024));
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "needle");

        var result = new WorkspaceSearch().Search(temp.Root, "needle");

        Assert.Contains("a.txt:1:", result.Text);
        Assert.DoesNotContain("huge.txt", result.Text);
    }

    [Fact]
    public void 上下文行会带出命中行前后()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), "before\nneedle\nafter\n");
        var search = new WorkspaceSearch();

        var result = search.Search(temp.Root, "needle", contextLines: 1);

        Assert.Contains("a.txt:1: before", result.Text);
        Assert.Contains("a.txt:2: needle", result.Text);
        Assert.Contains("a.txt:3: after", result.Text);
    }

    [Fact]
    public void 结果上限与同文件上限生效()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "a.txt"), string.Join('\n', Enumerable.Repeat("needle", 30)));
        var search = new WorkspaceSearch();

        var result = search.Search(temp.Root, "needle", maxResults: 5);

        var lines = result.Text.Split('\n').Where(line => line.StartsWith("a.txt:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, lines.Length);
        Assert.Contains("结果已达上限", result.Text);
    }

    [Fact]
    public void glob星号只匹配当前层双星匹配任意层()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src", "nested"));
        File.WriteAllText(Path.Combine(temp.Root, "src", "a.cs"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Root, "src", "nested", "b.cs"), string.Empty);
        var search = new WorkspaceSearch();

        Assert.Equal(["src/a.cs"], search.Glob(temp.Root, "src/*.cs"));
        Assert.Equal(["src/a.cs", "src/nested/b.cs"], search.Glob(temp.Root, "**/*.cs"));
    }

    [Fact]
    public void glob问号匹配单个字符且大小写不敏感()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "AB.cs"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Root, "ABC.cs"), string.Empty);
        var search = new WorkspaceSearch();

        Assert.Equal(["AB.cs"], search.Glob(temp.Root, "?B.cs"));
        Assert.Equal(["AB.cs"], search.Glob(temp.Root, "ab.?s"));
    }

    [Fact]
    public void glob结果按路径排序并受上限约束()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "c.cs", "a.cs", "b.cs" })
        {
            File.WriteAllText(Path.Combine(temp.Root, name), string.Empty);
        }

        var search = new WorkspaceSearch();

        Assert.Equal(["a.cs", "b.cs", "c.cs"], search.Glob(temp.Root, "*.cs"));
        Assert.Equal(["a.cs", "b.cs"], search.Glob(temp.Root, "*.cs", maxResults: 2));
    }

    [Fact]
    public void 目录树按深度与上限返回且排除构建目录()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src", "Api"));
        Directory.CreateDirectory(Path.Combine(temp.Root, "bin"));
        File.WriteAllText(Path.Combine(temp.Root, "src", "Api", "Options.cs"), string.Empty);
        var search = new WorkspaceSearch();

        var result = search.ListTree(temp.Root, ".", depth: 3, maxEntries: 20);

        Assert.Contains("src/Api/", result);
        Assert.Contains("src/Api/Options.cs", result);
        Assert.DoesNotContain("bin/", result);
    }

    [Fact]
    public void 目录树深度为零或负数按一处理()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src"));
        File.WriteAllText(Path.Combine(temp.Root, "src", "a.cs"), string.Empty);
        var search = new WorkspaceSearch();

        var result = search.ListTree(temp.Root, ".", depth: 0);

        Assert.Contains("src/", result);
        Assert.DoesNotContain("a.cs", result);
    }
}
