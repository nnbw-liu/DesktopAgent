using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P4：导出只落图片 / SVG / WebM，去掉路径部分并处理重名。</summary>
public sealed class ArtifactExportPolicyTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _exports;

    public ArtifactExportPolicyTests()
    {
        _exports = Path.Combine(_root.Root, "exports");
        Directory.CreateDirectory(_exports);
    }

    [Theory]
    [InlineData("diagram.png")]
    [InlineData("diagram.JPG")]
    [InlineData("diagram.jpeg")]
    [InlineData("diagram.webp")]
    [InlineData("diagram.svg")]
    [InlineData("motion.webm")]
    public void 接受产物导出扩展名(string name)
    {
        Assert.True(ArtifactExportPolicy.TryCreateTarget(
            _exports,
            name,
            DateTimeOffset.UnixEpoch,
            out var target));
        Assert.Equal(Path.Combine(_exports, name), target);
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("script.js")]
    [InlineData("no-extension")]
    [InlineData("")]
    public void 拒绝不支持或可疑文件名(string name)
    {
        Assert.False(ArtifactExportPolicy.TryCreateTarget(
            _exports,
            name,
            DateTimeOffset.UnixEpoch,
            out _));
    }

    [Fact]
    public void 路径段被去目录且不写_export_外()
    {
        Assert.True(ArtifactExportPolicy.TryCreateTarget(
            _exports,
            "../escape.png",
            DateTimeOffset.UnixEpoch,
            out var target));
        Assert.Equal(Path.Combine(_exports, "escape.png"), target);
        Assert.StartsWith(Path.GetFullPath(_exports) + Path.DirectorySeparatorChar, target);
    }

    [Fact]
    public void 重名时追加时间戳()
    {
        File.WriteAllText(Path.Combine(_exports, "diagram.png"), "x");
        var now = new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(8));

        Assert.True(ArtifactExportPolicy.TryCreateTarget(_exports, "diagram.png", now, out var target));
        Assert.Equal(
            Path.Combine(_exports, "diagram-20261027-140000000.png"),
            target);
        Assert.NotEqual(Path.GetFullPath(Path.Combine(_exports, "diagram.png")), Path.GetFullPath(target!));
    }

    public void Dispose() => _root.Dispose();
}
