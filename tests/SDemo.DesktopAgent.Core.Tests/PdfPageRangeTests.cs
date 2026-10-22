using SDemo.DesktopAgent.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 页范围是“用户输入 → 沙箱实际读取”的唯一约定：去重、升序、上限 50 页，
/// 越界必须报错而不是静默读别的页。
/// </summary>
public sealed class PdfPageRangeTests
{
    [Fact]
    public void 空值默认前5页且不超过总页数()
    {
        Assert.Equal([1, 2, 3, 4, 5], PdfPageRange.Parse(null, 10));
        Assert.Equal([1, 2], PdfPageRange.Parse("  ", 2));
    }

    [Fact]
    public void 支持单页区间与混合范围()
    {
        Assert.Equal([1, 2, 3, 7], PdfPageRange.Parse("3,1-2,7", 10));
    }

    [Fact]
    public void 去重并按升序返回()
    {
        Assert.Equal([2, 3, 4, 5], PdfPageRange.Parse("5,2-4,3", 6));
    }

    [Fact]
    public void 最多允许50页()
    {
        Assert.Equal(50, PdfPageRange.Parse("1-50", 80).Count);

        var error = Assert.Throws<PdfRangeException>(() => PdfPageRange.Parse("1-51", 80));
        Assert.Contains("50", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3-1")]
    [InlineData("6")]
    [InlineData("1-6")]
    [InlineData("a")]
    [InlineData("1,,x")]
    [InlineData("1-2-3")]
    public void 非法范围抛出明确错误(string value)
    {
        Assert.Throws<PdfRangeException>(() => PdfPageRange.Parse(value, 5));
    }

    [Fact]
    public void 语法校验不依赖真实页数()
    {
        Assert.True(PdfPageRange.TryValidateSyntax("1-5,8", out var error));
        Assert.Null(error);

        Assert.False(PdfPageRange.TryValidateSyntax("8-2", out error));
        Assert.Contains("页范围", error);
    }

    [Fact]
    public void 页数无效时拒绝()
    {
        Assert.Throws<PdfRangeException>(() => PdfPageRange.Parse("1", 0));
    }
}
