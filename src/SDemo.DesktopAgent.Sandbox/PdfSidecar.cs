using System.IO;
using System.Text;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// PDF 文本 sidecar 的落盘约定：与 PDF 同目录、同名，只多一个 <c>.txt</c>。
/// 路径可推导，因此附件记录里只需要保存它在不在，不需要额外命名规则。
/// </summary>
public static class PdfSidecar
{
    public const string Suffix = ".txt";

    public static string PathFor(string pdfPath) => pdfPath + Suffix;

    /// <summary>写 sidecar；失败返回 null（sidecar 只是增强信息，不能阻塞上传）。</summary>
    public static async Task<string?> WriteAsync(
        string pdfPath,
        PdfAttachmentText text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(pdfPath);
            await File.WriteAllTextAsync(path, text.Text, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }
}
