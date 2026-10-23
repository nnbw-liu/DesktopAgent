using System.IO;
using Microsoft.AspNetCore.Components.Forms;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 上传附件的落盘位置：{AppData}\uploads\{sessionId}\。
/// 数据库只存路径，文件本身留在这里，重启后仍可读取 / 显示。
/// 放在 UI 工程是因为它只处理 Blazor 的 <see cref="IBrowserFile"/>。
/// PDF 会额外尝试提取前几页文本（经沙箱），成功时写 sidecar；提取失败不影响上传。
/// </summary>
public sealed class AttachmentStore
{
    public const long MaxFileSize = 25 * 1024 * 1024;

    private readonly string _root;
    private readonly IReadOnlyList<IPdfAttachmentExtractor> _pdfExtractors;

    public AttachmentStore(string root)
        : this(root, [])
    {
    }

    public AttachmentStore(
        string root,
        IEnumerable<IPdfAttachmentExtractor> pdfExtractors)
    {
        _root = root;
        _pdfExtractors = pdfExtractors.ToArray();
    }

    public AttachmentStore(
        IAppPaths paths,
        IEnumerable<IPdfAttachmentExtractor> pdfExtractors)
        : this(paths.UploadsRoot, pdfExtractors)
    {
    }

    public async Task<ChatAttachment> SaveAsync(
        IBrowserFile file,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var folder = string.IsNullOrWhiteSpace(sessionId) ? "draft" : sessionId;
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(file.Name);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, id + Path.GetExtension(fileName));

        await using (var input = file.OpenReadStream(MaxFileSize, cancellationToken))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        var displayName = string.IsNullOrWhiteSpace(fileName) ? "附件" : fileName;

        var sidecar = await TryExtractPdfAsync(path, cancellationToken).ConfigureAwait(false);

        return new ChatAttachment
        {
            Id = id,
            FileName = displayName,
            FilePath = path,
            Size = file.Size,
            ExtractedTextPath = sidecar?.Path,
            PageCount = sidecar?.PageCount,
            HasTextLayer = sidecar?.HasTextLayer
        };
    }

    public void Delete(ChatAttachment attachment)
    {
        TryDelete(attachment.FilePath);
        TryDelete(PdfSidecar.PathFor(attachment.FilePath));
        TryDelete(attachment.ExtractedTextPath);
    }

    /// <summary>1.2 KB / 3.4 MB 这类短文本。</summary>
    public static string FormatSize(long bytes) => ByteSize.FormatSize(bytes);

    /// <summary>
    /// 提取结果只在一次上传里有效：不放到字段上，避免并发上传把两个附件的元信息串在一起。
    /// </summary>
    private async Task<PdfAttachmentSidecar?> TryExtractPdfAsync(
        string pdfPath,
        CancellationToken cancellationToken)
    {
        if (_pdfExtractors.Count == 0
            || !string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var result = await _pdfExtractors[0].ExtractAsync(pdfPath, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                return null;
            }

            var sidecarPath = await PdfSidecar.WriteAsync(pdfPath, result, cancellationToken)
                .ConfigureAwait(false);

            return sidecarPath is null
                ? null
                : new PdfAttachmentSidecar(sidecarPath, result.PageCount, result.HasTextLayer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // sidecar 只是增强信息：写不进去 / 提取失败都保留二进制附件。
            return null;
        }
    }

    private sealed record PdfAttachmentSidecar(string Path, int PageCount, bool HasTextLayer);

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着，不影响主流程。
        }
    }
}
