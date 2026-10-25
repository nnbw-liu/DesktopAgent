using System.IO;
using SDemo.DesktopAgent.Core.Services;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>`@` 候选的两个来源：路径索引（枚举 + 排除 + 缓存）与排序规则。</summary>
public sealed class ComposerReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sdemo-composer-refs",
        Guid.NewGuid().ToString("N"));

    public ComposerReferenceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "Core"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));

        File.WriteAllText(Path.Combine(_root, "README.md"), "readme");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class P {}");
        File.WriteAllText(Path.Combine(_root, "src", "Core", "Budget.cs"), "class B {}");
        File.WriteAllText(Path.Combine(_root, "bin", "app.dll"), "binary");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "index.js"), "js");
    }

    [Fact]
    public void 枚举只给相对路径且目录带尾斜杠()
    {
        var index = new WorkspaceReferenceIndex();

        var entries = index.List(_root);

        Assert.Contains("README.md", entries);
        Assert.Contains("src/", entries);
        Assert.Contains("src/Program.cs", entries);
        Assert.Contains("src/Core/", entries);
        Assert.Contains("src/Core/Budget.cs", entries);
        Assert.All(entries, entry => Assert.False(Path.IsPathRooted(entry)));
    }

    [Fact]
    public void 构建产物与依赖目录被排除()
    {
        var index = new WorkspaceReferenceIndex();

        var entries = index.List(_root);

        Assert.DoesNotContain(entries, entry => entry.StartsWith("bin/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("node_modules/", StringComparison.Ordinal));
    }

    [Fact]
    public void 没有工作区时返回空()
    {
        var index = new WorkspaceReferenceIndex();

        Assert.Empty(index.List(null));
        Assert.Empty(index.List(string.Empty));
        Assert.Empty(index.List(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void 缓存期内不重新扫盘失效后重新扫()
    {
        var now = DateTimeOffset.UnixEpoch;
        var index = new WorkspaceReferenceIndex(() => now);

        var first = index.List(_root);

        File.WriteAllText(Path.Combine(_root, "New.cs"), "new");

        // 缓存期内：看不到新文件。
        Assert.Same(first, index.List(_root));

        // 过期后：重新枚举。
        now = now.AddSeconds(31);
        var refreshed = index.List(_root);
        Assert.Contains("New.cs", refreshed);

        // 显式失效：立刻重新扫。
        File.WriteAllText(Path.Combine(_root, "Newer.cs"), "newer");
        index.Invalidate();
        Assert.Contains("Newer.cs", index.List(_root));
    }

    [Fact]
    public void 前缀命中排在子序列之前()
    {
        var items = new[] { "src/Core/Budget.cs", "src/Program.cs", "docs/program-guide.md" };

        var ranked = ComposerMatch.Rank(items, item => item, "prog", 3);

        Assert.Equal("src/Program.cs", ranked[0]);
    }

    [Fact]
    public void 大小写与有序子序列匹配()
    {
        var items = new[] { "README.md", "src/Program.cs" };

        Assert.Equal("README.md", ComposerMatch.Rank(items, item => item, "readme", 1)[0]);
        Assert.Equal("src/Program.cs", ComposerMatch.Rank(items, item => item, "spc", 1)[0]);
    }

    [Fact]
    public void 空查询按原顺序截断没命中返回空()
    {
        var items = new[] { "a.cs", "b.cs", "c.cs" };

        Assert.Equal(["a.cs", "b.cs"], ComposerMatch.Rank(items, item => item, null, 2));
        Assert.Empty(ComposerMatch.Rank(items, item => item, "zzz", 3));
        Assert.Empty(ComposerMatch.Rank(items, item => item, "a", 0));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结论。
        }
    }
}
