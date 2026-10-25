using System.IO;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 上传 .docx / .xlsx / .pptx 时自动转成 Markdown sidecar（默认前 24000 字符），
/// 让「丢一个 PPT 进来直接问」不需要模型再手写一次工具调用。
/// 解析失败返回 null，上传流程照常保留原始附件。
/// </summary>
public sealed class OfficeAttachmentExtractor : IOfficeAttachmentExtractor
{
    public const int MaxChars = 24_000;

    public Task<OfficeAttachmentText?> ExtractAsync(
        string officePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(officePath);
            var format = OfficeFile.FormatOf(officePath);

            if (format is not ("docx" or "xlsx" or "pptx"))
            {
                return null;
            }

            try
            {
                var text = OfficeReader.Read(officePath, maxChars: MaxChars);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var truncated = text.Contains("已截断", StringComparison.Ordinal);
                return new OfficeAttachmentText(text, format, truncated);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 上传增强信息：损坏 / 加密 / 旧格式都只保留原附件，绝不让上传失败。
                return null;
            }
        }, cancellationToken);
    }
}
