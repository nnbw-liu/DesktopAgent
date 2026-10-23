using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Execution;

/// <summary>解析 dotnet build / MSBuild 的英文与中文摘要。</summary>
public static class BuildResultParser
{
    private static readonly Regex ErrorSummary = new(
        @"(?<count>\d+)\s*(?:Error\(s\)|个错误)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WarningSummary = new(
        @"(?<count>\d+)\s*(?:Warning\(s\)|个警告)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorLine = new(
        @"^\s*.*\berror\b\s+[A-Z]{1,8}\d+.*$",
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

        var errors = ReadCount(ErrorSummary, output);
        var warnings = ReadCount(WarningSummary, output);

        if (errors < 0)
        {
            errors = ErrorLine.Matches(output).Count;
        }

        var top = ErrorLine.Matches(output)
            .Select(match => match.Value.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        return new VerificationRecord(
            VerificationRecord.BuildKind,
            command,
            Success: exitCode == 0 && errors == 0,
            ExitCode: exitCode,
            Errors: errors,
            Warnings: Math.Max(0, warnings),
            Passed: 0,
            Failed: 0,
            TopIssues: top,
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
