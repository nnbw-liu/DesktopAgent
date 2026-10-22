using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>上传附件时自动提取的 PDF 文本。文本由实现格式化，调用方只负责落盘。</summary>
public sealed record PdfAttachmentText(
    string Text,
    int PageCount,
    bool HasTextLayer,
    bool Truncated = false);

/// <summary>
/// 上传 PDF 时提取前几页文本（在沙箱内完成）。
/// 实现必须“失败不影响上传”：沙箱不可用 / 超时 / 解析失败都返回 null，由调用方保留二进制附件。
/// </summary>
public interface IPdfAttachmentExtractor
{
    Task<PdfAttachmentText?> ExtractAsync(string pdfPath, CancellationToken cancellationToken = default);
}
