using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 上传 Office 文档时自动提取的 Markdown 文本。
/// <paramref name="Format"/> 是 docx / xlsx / pptx，用于界面与上下文说明。
/// </summary>
public sealed record OfficeAttachmentText(
    string Text,
    string Format,
    bool Truncated = false);

/// <summary>
/// 上传 .docx / .xlsx / .pptx 时把内容转成 Markdown，随消息一起进入上下文。
/// 实现必须「失败不影响上传」：文件损坏 / 加密 / 解析失败都返回 null，由调用方保留原始附件。
/// </summary>
public interface IOfficeAttachmentExtractor
{
    Task<OfficeAttachmentText?> ExtractAsync(string officePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Office 文本 sidecar 的落盘约定：与源文件同目录、同名，后缀 <c>.office.md</c>。
/// 附件记录里把路径存进 <see cref="Models.ChatAttachment.ExtractedTextPath"/>，
/// 删除附件时就能和 PDF sidecar 走同一条清理路径。
/// </summary>
public static class OfficeSidecar
{
    public const string Suffix = ".office.md";

    public static string PathFor(string officePath) => officePath + Suffix;

    /// <summary>写 sidecar；失败返回 null（sidecar 只是增强信息，不能阻塞上传）。</summary>
    public static async Task<string?> WriteAsync(
        string officePath,
        OfficeAttachmentText text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(officePath);
            await File.WriteAllTextAsync(path, text.Text, new System.Text.UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }
}
