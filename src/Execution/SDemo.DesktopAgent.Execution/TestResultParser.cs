using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Execution;

/// <summary>解析 dotnet test / xUnit / npm test 摘要；解析失败仍保留退出码。</summary>
public static class TestResultParser
{
    private static readonly Regex PassedCount = new(
        @"(?:Passed|通过)\s*:\s*(?<count>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FailedCount = new(
        @"(?:Failed|失败)\s*:\s*(?<count>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex XunitFailure = new(
        @"(?<name>[\w.]+)\s*\[FAIL\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FailedTest = new(
        @"^\s*Failed\s+(?<name>[\w.]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public static VerificationRecord Parse(
        string command,
        string stdout,
        string stderr,
        int exitCode,
        long durationMs,
        DateTime? at = null)
    {
        var output = string.Join('\n', stdout, stderr);
        var passed = ReadCount(PassedCount, output);
        var failed = ReadCount(FailedCount, output);

        var issues = XunitFailure.Matches(output)
            .Select(match => match.Groups["name"].Value)
            .Concat(FailedTest.Matches(output).Select(match => match.Groups["name"].Value))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        if (issues.Length == 0 && failed < 0 && exitCode != 0)
        {
            // 解析不出结构：保留最后 5 行，至少让模型看到失败原因。
            issues = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(5)
                .Select(line => line.Trim())
                .ToArray();
        }

        var knownPassed = Math.Max(0, passed);
        var knownFailed = Math.Max(0, failed);

        return new VerificationRecord(
            VerificationRecord.TestKind,
            command,
            Success: exitCode == 0 && knownFailed == 0,
            ExitCode: exitCode,
            Errors: 0,
            Warnings: 0,
            Passed: knownPassed,
            Failed: knownFailed,
            TopIssues: issues,
            At: at ?? DateTime.Now,
            DurationMs: durationMs);
    }

    private static int ReadCount(Regex regex, string output)
    {
        var match = regex.Match(output);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : -1;
    }
}
