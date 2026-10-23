using System.Text;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 附件 PDF 文本提取（S5）：走 <see cref="ISandboxJobService.ExtractPdfAsync"/>，
/// 由沙箱内的 SandboxHost 解析前几页。任何失败（沙箱不可用、加密、扫描件、超时）
/// 都返回 null，不阻塞上传。
/// </summary>
public sealed class SandboxPdfAttachmentExtractor : IPdfAttachmentExtractor
{
    /// <summary>上传不应该被 PDF 解析拖住：最多等 10 秒，超时就跳过，后续可用 pdf_read 重试。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ISandboxJobService _jobs;
    private readonly ISettingsService _settings;

    public SandboxPdfAttachmentExtractor(ISandboxJobService jobs, ISettingsService settings)
    {
        _jobs = jobs;
        _settings = settings;
    }

    public async Task<PdfAttachmentText?> ExtractAsync(
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current.Sandbox;

        if (!settings.Enabled || !settings.PdfAutoExtract)
        {
            return null;
        }

        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var extraction = await _jobs.ExtractPdfAsync(
                    pdfPath,
                    pages: $"1-{PdfLimits.AttachmentPages}",
                    maxChars: PdfLimits.AttachmentChars,
                    timeout.Token)
                .ConfigureAwait(false);

            if (!extraction.Ok || !extraction.HasTextLayer)
            {
                return null;
            }

            var text = BuildText(extraction);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new PdfAttachmentText(
                    text,
                    extraction.PageCount,
                    extraction.HasTextLayer,
                    extraction.Truncated);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 提取失败保留二进制附件，不影响上传。
            return null;
        }
    }

    private static string BuildText(PdfExtraction extraction)
    {
        var builder = new StringBuilder();

        foreach (var page in extraction.Pages.Where(page => !string.IsNullOrWhiteSpace(page.Text)))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append("--- page ").Append(page.Number).AppendLine(" ---");
            builder.AppendLine(page.Text.TrimEnd());
        }

        if (extraction.Truncated)
        {
            builder.AppendLine();
            builder.AppendLine("（本次只提取了前几页；可用 pdf_read 读取其他页）");
        }

        return builder.ToString().TrimEnd();
    }
}
