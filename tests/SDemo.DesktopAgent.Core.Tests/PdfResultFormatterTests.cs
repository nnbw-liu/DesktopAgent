using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>PDF 工具结果进入模型上下文前必须可读，并且明确区分“加密 / 无文本层 / 解析失败”。</summary>
public sealed class PdfResultFormatterTests
{
    [Fact]
    public void 正常结果包含页数与分页文本()
    {
        var extraction = new PdfExtraction(
            Ok: true,
            PageCount: 3,
            Encrypted: false,
            HasTextLayer: true,
            Pages: [new PdfPageText(1, "hello"), new PdfPageText(2, "world")],
            Truncated: false,
            Message: null);

        var text = PdfResultFormatter.FormatRead("report.pdf", extraction);

        Assert.Contains("PDF: report.pdf", text);
        Assert.Contains("页数: 3", text);
        Assert.Contains("文本层: 有", text);
        Assert.Contains("--- page 1 ---", text);
        Assert.Contains("hello", text);
        Assert.Contains("--- page 2 ---", text);
        Assert.Contains("world", text);
        Assert.DoesNotContain("已截断", text);
    }

    [Fact]
    public void 截断时明确标记()
    {
        var extraction = new PdfExtraction(
            Ok: true,
            PageCount: 9,
            Encrypted: false,
            HasTextLayer: true,
            Pages: [new PdfPageText(1, "text")],
            Truncated: true,
            Message: null);

        Assert.Contains("已截断", PdfResultFormatter.FormatRead("a.pdf", extraction));
    }

    [Fact]
    public void 页范围按连续区间显示()
    {
        var extraction = new PdfExtraction(
            Ok: true,
            PageCount: 9,
            Encrypted: false,
            HasTextLayer: true,
            Pages:
            [
                new PdfPageText(1, "a"),
                new PdfPageText(2, "b"),
                new PdfPageText(3, "c"),
                new PdfPageText(5, "e")
            ],
            Truncated: false,
            Message: null);

        Assert.Contains("页范围: 1-3,5", PdfResultFormatter.FormatRead("doc.pdf", extraction));
    }

    [Fact]
    public void 无文本层给出OCR提示且不展示空页()
    {
        var extraction = new PdfExtraction(
            Ok: true,
            PageCount: 2,
            Encrypted: false,
            HasTextLayer: false,
            Pages: [new PdfPageText(1, " "), new PdfPageText(2, "")],
            Truncated: false,
            Message: null);

        var text = PdfResultFormatter.FormatRead("scan.pdf", extraction);

        Assert.Contains("未检测到文本层", text);
        Assert.Contains("OCR", text);
        Assert.DoesNotContain("--- page", text);
    }

    [Fact]
    public void 加密结果固定提示不支持密码()
    {
        var extraction = new PdfExtraction(
            Ok: false,
            PageCount: 0,
            Encrypted: true,
            HasTextLayer: false,
            Pages: [],
            Truncated: false,
            Message: "PDF 已加密，当前不支持在对话中传密码。");

        Assert.Equal(
            "PDF 已加密，当前不支持在对话中传密码。",
            PdfResultFormatter.FormatRead("secret.pdf", extraction));
    }

    [Fact]
    public void 解析失败保留沙箱给出的原因()
    {
        var extraction = new PdfExtraction(
            Ok: false,
            PageCount: 0,
            Encrypted: false,
            HasTextLayer: false,
            Pages: [],
            Truncated: false,
            Message: "PDF 解析失败：文件损坏");

        Assert.Equal("PDF 解析失败：文件损坏", PdfResultFormatter.FormatRead("broken.pdf", extraction));
    }

    [Fact]
    public void 没有原因时也给出通用失败文案()
    {
        var extraction = new PdfExtraction(false, 0, false, false, [], false, null);

        Assert.Equal("PDF 解析失败：未知原因。", PdfResultFormatter.FormatInfo("x.pdf", extraction));
    }

    [Fact]
    public void Info不输出分页文本()
    {
        var extraction = new PdfExtraction(
            Ok: true,
            PageCount: 12,
            Encrypted: false,
            HasTextLayer: true,
            Pages: [new PdfPageText(1, "sample")],
            Truncated: false,
            Message: null);

        var text = PdfResultFormatter.FormatInfo("doc.pdf", extraction);

        Assert.Contains("页数: 12", text);
        Assert.DoesNotContain("--- page", text);
    }
}
