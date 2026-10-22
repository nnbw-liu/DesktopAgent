using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// PDF 附件进入上下文的方式：优先用沙箱提取的 sidecar，同时保留原文件路径，
/// 这样模型既能直接读前几页，也能在需要时调用 pdf_read 读其他页。
/// </summary>
public sealed class PdfAttachmentContextTests
{
    [Fact]
    public void 有sidecar时内联提取文本并给出总页数与路径()
    {
        using var temp = new TempDirectory();
        var pdf = WriteBinary(temp.Root, "report.pdf");
        var sidecar = pdf + ".txt";
        File.WriteAllText(sidecar, "--- page 1 ---\nhello pdf");

        var text = Map("看看这个 PDF", new ChatAttachment
        {
            FileName = "report.pdf",
            FilePath = pdf,
            Size = 1024,
            ExtractedTextPath = sidecar,
            PageCount = 12,
            HasTextLayer = true
        });

        Assert.Contains("--- page 1 ---", text);
        Assert.Contains("hello pdf", text);
        Assert.Contains("PDF 共 12 页", text);
        Assert.Contains(pdf, text);
        Assert.DoesNotContain("二进制或无法读取", text);
    }

    [Fact]
    public void 扫描件不内联内容并提示需要OCR()
    {
        using var temp = new TempDirectory();
        var pdf = WriteBinary(temp.Root, "scan.pdf");

        var text = Map("看看这个", new ChatAttachment
        {
            FileName = "scan.pdf",
            FilePath = pdf,
            Size = 2048,
            PageCount = 5,
            HasTextLayer = false
        });

        Assert.Contains("未检测到文本层", text);
        Assert.Contains("OCR", text);
        Assert.Contains("PDF 共 5 页", text);
        Assert.DoesNotContain("```text", text);
    }

    [Fact]
    public void 提取失败时保留路径并提示可用pdf_read重试()
    {
        using var temp = new TempDirectory();
        var pdf = WriteBinary(temp.Root, "pending.pdf");

        var text = Map("读一下", new ChatAttachment
        {
            FileName = "pending.pdf",
            FilePath = pdf,
            Size = 4096
        });

        Assert.Contains("PDF 文本提取未完成", text);
        Assert.Contains("pdf_read", text);
        Assert.Contains(pdf, text);
    }

    [Fact]
    public void 普通文本附件仍按原逻辑内联()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "notes.txt");
        File.WriteAllText(file, "plain text");

        var text = Map("读一下", new ChatAttachment
        {
            FileName = "notes.txt",
            FilePath = file,
            Size = 10
        });

        Assert.Contains("plain text", text);
        Assert.DoesNotContain("pdf_read", text);
    }

    private static string Map(string content, ChatAttachment attachment)
    {
        var message = new ChatMessage(ChatMessageRole.User, content)
        {
            Attachments = [attachment]
        };

        return AgentMessageMapper.ToRequestMessage(message).Text;
    }

    private static string WriteBinary(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46, 0x00, 0x01]);
        return path;
    }
}
