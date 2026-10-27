namespace SDemo.DesktopAgent.Services;

/// <summary>一张视觉复核截图；路径是 Archify 写出的绝对路径。</summary>
public sealed record ArchifyScreenshot(
    string Path,
    int Width,
    int Height,
    string Theme,
    string ResolvedTheme);

/// <summary>
/// 用户显式触发的一次视觉复核结果。成功只代表<b>自动浏览器校验 + 截图</b>完成；
/// <see cref="VisualReview"/> 仍是 <c>pending</c> 时，绝不能声称做过人工视觉复核。
/// </summary>
public sealed record ArchifyReviewResult(
    bool Success,
    string Message,
    string? Status = null,
    string? VisualReview = null,
    string? ContactSheet = null,
    string? Receipt = null,
    string? OutputDirectory = null,
    IReadOnlyList<ArchifyScreenshot>? Screenshots = null)
{
    public static ArchifyReviewResult Fail(string message) => new(false, message);
}

/// <summary>
/// 用户点击「视觉复核」时，在宿主上跑 Archify 的 <c>visual-check</c>（自动浏览器检查 +
/// 四张明暗主题截图）。不是模型工具，不自动触发；结果只如实报告，不替代人工判断。
/// </summary>
public interface IArchifyReviewService
{
    Task<ArchifyReviewResult> ReviewAsync(
        string workspaceRoot,
        string artifactRelativePath,
        CancellationToken cancellationToken = default);
}
