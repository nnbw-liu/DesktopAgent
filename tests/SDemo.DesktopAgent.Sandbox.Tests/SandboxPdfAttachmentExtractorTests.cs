using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S5 附件提取：只在前几页有文本时写 sidecar；沙箱关闭 / 提取失败都返回 null，
/// 由 AttachmentStore 保留二进制附件，不阻塞上传。
/// </summary>
public sealed class SandboxPdfAttachmentExtractorTests
{
    [Fact]
    public async Task 成功时返回文本与页数并按默认页数请求()
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "report.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

        var jobs = new FakeSandboxJobService
        {
            Result = new PdfExtraction(
                Ok: true,
                PageCount: 12,
                Encrypted: false,
                HasTextLayer: true,
                Pages: [new PdfPageText(1, "page one"), new PdfPageText(2, "page two")],
                Truncated: false,
                Message: null)
        };
        var extractor = new SandboxPdfAttachmentExtractor(jobs, new TestSettingsService());

        var result = await extractor.ExtractAsync(pdf);

        Assert.NotNull(result);
        Assert.Equal(12, result!.PageCount);
        Assert.True(result.HasTextLayer);
        Assert.Contains("--- page 1 ---", result.Text);
        Assert.Contains("page one", result.Text);
        Assert.DoesNotContain("只提取了前几页", result.Text);

        var call = Assert.Single(jobs.PdfCalls);
        Assert.Equal(pdf, call.Path);
        Assert.Equal("1-3", call.Pages);
        Assert.Equal(PdfLimits.AttachmentChars, call.MaxChars);
    }

    [Fact]
    public async Task 截断时在sidecar里提示可读取其他页()
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "long.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

        var jobs = new FakeSandboxJobService
        {
            Result = new PdfExtraction(
                true, 30, false, true, [new PdfPageText(1, "text")], Truncated: true, Message: null)
        };
        var extractor = new SandboxPdfAttachmentExtractor(jobs, new TestSettingsService());

        var result = await extractor.ExtractAsync(pdf);

        Assert.NotNull(result);
        Assert.Contains("pdf_read", result!.Text);
    }

    [Fact]
    public async Task 关闭自动提取时不调用沙箱()
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "skip.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

        var settings = new TestSettingsService();
        settings.Current.Sandbox.PdfAutoExtract = false;
        var jobs = new FakeSandboxJobService();
        var extractor = new SandboxPdfAttachmentExtractor(jobs, settings);

        Assert.Null(await extractor.ExtractAsync(pdf));
        Assert.Empty(jobs.PdfCalls);
    }

    [Fact]
    public async Task 沙箱关闭时不调用沙箱()
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "disabled.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

        var settings = new TestSettingsService();
        settings.Current.Sandbox.Enabled = false;
        var jobs = new FakeSandboxJobService();
        var extractor = new SandboxPdfAttachmentExtractor(jobs, settings);

        Assert.Null(await extractor.ExtractAsync(pdf));
        Assert.Empty(jobs.PdfCalls);
    }

    [Fact]
    public async Task 解析失败或没有文本层时返回null()
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "broken.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

        var jobs = new FakeSandboxJobService
        {
            Result = new PdfExtraction(false, 0, false, false, [], false, "PDF 解析失败：损坏")
        };
        var extractor = new SandboxPdfAttachmentExtractor(jobs, new TestSettingsService());
        Assert.Null(await extractor.ExtractAsync(pdf));

        jobs.Result = new PdfExtraction(true, 2, false, false, [], false, null);
        Assert.Null(await extractor.ExtractAsync(pdf));
    }

    [Fact]
    public async Task 非PDF扩展名直接跳过()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "notes.txt");
        File.WriteAllText(file, "text");

        var jobs = new FakeSandboxJobService();
        var extractor = new SandboxPdfAttachmentExtractor(jobs, new TestSettingsService());

        Assert.Null(await extractor.ExtractAsync(file));
        Assert.Empty(jobs.PdfCalls);
    }
}

