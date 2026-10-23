using System.Diagnostics;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace SDemo.DesktopAgent.SandboxHost;

/// <summary>
/// PDF 命令（S5）。在沙箱内的 helper 进程里解析 PDF：崩溃 / 损坏 / 加密都不会拖垮宿主。
/// 结果写 <c>output/pdf.json</c>（原子写）；<c>result.json</c> 只是执行状态，供宿主导出诊断信息。
/// </summary>
internal static class PdfCommand
{
    /// <summary>容错解析：损坏但可救的 PDF 尽量读出文本，字体缺失不整体失败。</summary>
    private static ParsingOptions ParsingOptions => new()
    {
        UseLenientParsing = true,
        SkipMissingFonts = true
    };

    public static SandboxResultDocument Run(SandboxJobDocument job, string jobRoot)
    {
        var stopwatch = Stopwatch.StartNew();

        var pdfPath = SandboxPathGuard.ResolveUnder(jobRoot, job.InputFile, "input");
        if (!File.Exists(pdfPath))
        {
            throw new SandboxProtocolException($"PDF 不存在：{job.InputFile}");
        }

        var maxChars = Math.Clamp(job.MaxChars, 1, PdfLimits.MaxChars);

        PdfResultDocument document;
        try
        {
            document = job.Mode == SandboxJobMode.PdfInfo
                ? Info(job.JobId, pdfPath, maxChars)
                : Extract(job.JobId, pdfPath, job.Pages, maxChars);
        }
        catch (PdfDocumentEncryptedException)
        {
            document = Failure(job.JobId, "PDF 已加密，当前不支持在对话中传密码。", encrypted: true);
        }
        catch (PdfRangeException ex)
        {
            document = Failure(job.JobId, ex.Message, encrypted: false);
        }
        catch (Exception ex)
        {
            // 损坏 / 截断 / 解析器内部异常：写进 pdf.json，宿主按“解析失败”展示，不升级成协议错误。
            document = Failure(job.JobId, "PDF 解析失败：" + ex.Message, encrypted: false);
        }

        stopwatch.Stop();

        var outputDirectory = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        ResultWriter.WriteAtomic(
            Path.Combine(outputDirectory, SandboxProtocol.PdfResultFileName),
            document);

        return new SandboxResultDocument
        {
            JobId = job.JobId,
            ExitCode = document.Ok ? 0 : 1,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Error = document.Ok ? null : document.Message
        };
    }

    private static PdfResultDocument Extract(string jobId, string pdfPath, string? pages, int maxChars)
    {
        using var document = PdfDocument.Open(pdfPath, ParsingOptions);

        var pageNumbers = PdfPageRange.Parse(pages, document.NumberOfPages);
        var result = new List<PdfPageText>(pageNumbers.Count);
        var total = 0;
        var truncated = false;

        foreach (var number in pageNumbers)
        {
            var text = document.GetPage(number).Text ?? string.Empty;

            if (total + text.Length > maxChars)
            {
                text = text[..Math.Max(0, maxChars - total)];
                result.Add(new PdfPageText(number, text));
                total = maxChars;
                truncated = true;
                break;
            }

            total += text.Length;
            result.Add(new PdfPageText(number, text));
        }

        return new PdfResultDocument
        {
            JobId = jobId,
            Ok = true,
            PageCount = document.NumberOfPages,
            Encrypted = false,
            HasTextLayer = result.Any(page => !string.IsNullOrWhiteSpace(page.Text)),
            Pages = result,
            Truncated = truncated,
            Message = null
        };
    }

    private static PdfResultDocument Info(string jobId, string pdfPath, int maxChars)
    {
        using var document = PdfDocument.Open(pdfPath, ParsingOptions);

        var sampleCount = Math.Min(PdfLimits.InfoSamplePages, document.NumberOfPages);
        var sample = new List<PdfPageText>(sampleCount);
        var total = 0;

        for (var number = 1; number <= sampleCount && total < maxChars; number++)
        {
            var text = document.GetPage(number).Text ?? string.Empty;
            text = text.Length > maxChars - total ? text[..(maxChars - total)] : text;
            total += text.Length;
            sample.Add(new PdfPageText(number, text));
        }

        return new PdfResultDocument
        {
            JobId = jobId,
            Ok = true,
            PageCount = document.NumberOfPages,
            Encrypted = false,
            HasTextLayer = sample.Any(page => !string.IsNullOrWhiteSpace(page.Text)),
            Pages = sample,
            Truncated = false,
            Message = null
        };
    }

    private static PdfResultDocument Failure(string jobId, string message, bool encrypted) =>
        new()
        {
            JobId = jobId,
            Ok = false,
            Encrypted = encrypted,
            HasTextLayer = false,
            Pages = [],
            Truncated = false,
            Message = message
        };
}
