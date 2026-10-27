namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 宿主提供的隔离预览面（P4）：把预览暂存目录映射到一个独立虚拟源，
/// 并允许预览内的导出动作把文件落到受控导出目录。
/// 默认实现不可用；WPF 宿主在 WebView2 初始化后接入。
/// </summary>
public interface IArtifactPreviewHost
{
    /// <summary>WebView2 已初始化并完成虚拟源映射。</summary>
    bool IsSupported { get; }

    /// <summary>独立源根，例如 <c>https://archify-preview.local/</c>。</summary>
    Uri? BaseUri { get; }

    /// <summary>预览暂存根目录；服务只在这里复制，不直接暴露工作区。</summary>
    string? StagingRoot { get; }

    /// <summary>预览内导出（PNG/SVG/WebM 等）的受控落盘目录。</summary>
    string? ExportsRoot { get; }
}

/// <summary>一次隔离预览的准备结果。</summary>
public sealed record ArtifactPreviewResult(
    bool Success,
    string Message,
    Uri? PreviewUri = null,
    string? StagedRoot = null,
    string? OriginalPath = null,
    bool CspApplied = false,
    string? OriginalSha256 = null,
    long Bytes = 0)
{
    public static ArtifactPreviewResult Fail(string message) => new(false, message);
}

/// <summary>
/// 把 <c>.archify/.../*.html</c> 复制到宿主暂存目录，注入 CSP 后返回独立源 URL。
/// 原产物不被修改；预览只用于展示，不是校验结论。
/// </summary>
public interface IArtifactPreviewService
{
    bool IsSupported { get; }

    Task<ArtifactPreviewResult> PrepareAsync(
        string workspaceRoot,
        string artifactRelativePath,
        CancellationToken cancellationToken = default);
}
