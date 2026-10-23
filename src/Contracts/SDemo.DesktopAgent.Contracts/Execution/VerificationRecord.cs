namespace SDemo.DesktopAgent.Execution;

/// <summary>一次构建 / 测试的验证结果（P4 §3.6）。解析失败时 Errors = -1。</summary>
public sealed record VerificationRecord(
    string Kind,
    string Command,
    bool Success,
    int ExitCode,
    int Errors,
    int Warnings,
    int Passed,
    int Failed,
    IReadOnlyList<string> TopIssues,
    DateTime At,
    long DurationMs)
{
    public const string BuildKind = "build";
    public const string TestKind = "test";

    /// <summary>状态包里的单行摘要；验证结果永远保留在状态包末尾。</summary>
    public string Format()
    {
        var icon = Success ? "成功" : "失败";
        var detail = Kind switch
        {
            BuildKind => $"{Errors} errors / {Warnings} warnings",
            TestKind => $"{Passed} passed / {Failed} failed",
            _ => $"{Passed} passed / {Failed} failed"
        };

        var issue = TopIssues.FirstOrDefault();
        var issueText = string.IsNullOrWhiteSpace(issue) ? string.Empty : $"：{Shorten(issue, 120)}";

        return $"{Shorten(Command, 120)} → {icon}（{detail}，{DurationMs / 1000.0:0.0}s，{At:HH:mm}）{issueText}";
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
