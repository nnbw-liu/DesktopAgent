using SDemo.DesktopAgent.Execution;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>dotnet test / npm test 结果解析：通过、失败、中文、未知输出。</summary>
public sealed class TestResultParserTests
{
    [Fact]
    public void 全部通过()
    {
        var record = TestResultParser.Parse(
            "dotnet test Demo.sln",
            "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12",
            string.Empty,
            0,
            2000);

        Assert.True(record.Success);
        Assert.Equal(12, record.Passed);
        Assert.Equal(0, record.Failed);
        Assert.Empty(record.TopIssues);
    }

    [Fact]
    public void 有失败时提取用例名()
    {
        var output = """
            [xUnit.net 00:00:01.00]     Demo.Tests.OptionsTests.RejectsEmpty [FAIL]
            Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13
            """;

        var record = TestResultParser.Parse("dotnet test", output, string.Empty, 1, 3000);

        Assert.False(record.Success);
        Assert.Equal(12, record.Passed);
        Assert.Equal(1, record.Failed);
        Assert.Contains("Demo.Tests.OptionsTests.RejectsEmpty", record.TopIssues);
    }

    [Fact]
    public void 中文摘要可解析()
    {
        var record = TestResultParser.Parse(
            "dotnet test",
            "已通过! - 失败:     0，通过:    10，已跳过:     0，总计:    10",
            string.Empty,
            0,
            1000);

        Assert.True(record.Success);
        Assert.Equal(10, record.Passed);
        Assert.Equal(0, record.Failed);
    }

    [Fact]
    public void npm测试回退末尾输出()
    {
        var record = TestResultParser.Parse(
            "npm test",
            "line1\nline2\n3 passing\n1 failing",
            string.Empty,
            1,
            500);

        Assert.False(record.Success);
        Assert.NotEmpty(record.TopIssues);
        Assert.Contains(record.TopIssues, line => line.Contains("failing"));
    }

    [Fact]
    public void 失败格式包含用例名()
    {
        var record = new VerificationRecord(
            VerificationRecord.TestKind,
            "dotnet test",
            false,
            1,
            0,
            0,
            12,
            1,
            ["OptionsTests.RejectsEmpty"],
            DateTime.Parse("2026-10-22T10:47:00+08:00"),
            1500);

        var text = record.Format();

        Assert.Contains("失败", text);
        Assert.Contains("12 passed / 1 failed", text);
        Assert.Contains("OptionsTests.RejectsEmpty", text);
    }
}
