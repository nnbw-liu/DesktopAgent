using SDemo.DesktopAgent.Execution;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>dotnet build 结果解析：英文 / 中文摘要、错误明细、未知输出。</summary>
public sealed class BuildResultParserTests
{
    [Fact]
    public void 英文零错误()
    {
        var record = BuildResultParser.Parse(
            "dotnet build Demo.sln",
            "Build succeeded.\n    0 Warning(s)\n    0 Error(s)",
            string.Empty,
            0,
            1234);

        Assert.True(record.Success);
        Assert.Equal(0, record.Errors);
        Assert.Equal(0, record.Warnings);
        Assert.Empty(record.TopIssues);
        Assert.Equal(VerificationRecord.BuildKind, record.Kind);
    }

    [Fact]
    public void 英文多错误提取首个定位()
    {
        var record = BuildResultParser.Parse(
            "dotnet build Demo.sln",
            "src/A.cs(12,5): error CS1002: ; expected\nsrc/B.cs(3,1): error CS0103: missing\n    2 Error(s)\n    1 Warning(s)",
            string.Empty,
            1,
            800);

        Assert.False(record.Success);
        Assert.Equal(2, record.Errors);
        Assert.Equal(1, record.Warnings);
        Assert.Contains("CS1002", record.TopIssues[0]);
        Assert.Contains("src", record.TopIssues[0]);
    }

    [Fact]
    public void 中文摘要可解析()
    {
        var record = BuildResultParser.Parse(
            "dotnet build",
            "已成功生成。\n    0 个警告\n    0 个错误",
            string.Empty,
            0,
            500);

        Assert.True(record.Success);
        Assert.Equal(0, record.Errors);
        Assert.Equal(0, record.Warnings);
    }

    [Fact]
    public void 无摘要时回退错误行且标记失败()
    {
        var record = BuildResultParser.Parse(
            "dotnet build",
            "something broke\nsrc/A.cs(1,1): error CS0001: bad",
            string.Empty,
            1,
            300);

        Assert.False(record.Success);
        Assert.Equal(1, record.Errors);
        Assert.Single(record.TopIssues);
    }

    [Fact]
    public void 状态包格式包含结果与耗时()
    {
        var record = new VerificationRecord(
            VerificationRecord.BuildKind,
            "dotnet build Demo.sln",
            true,
            0,
            0,
            2,
            0,
            0,
            [],
            DateTime.Parse("2026-10-22T10:45:00+08:00"),
            8400);

        var text = record.Format();

        Assert.Contains("dotnet build Demo.sln", text);
        Assert.Contains("成功", text);
        Assert.Contains("0 errors / 2 warnings", text);
        Assert.Contains("8.4s", text);
    }
}
