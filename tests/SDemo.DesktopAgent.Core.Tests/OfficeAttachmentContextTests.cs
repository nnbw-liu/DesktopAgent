using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// Office 附件进入上下文的方式：上传时提取的 Markdown sidecar 直接内联，
/// 同时保留原文件路径，截断后模型还能用 office_read 按页 / 按表续读。
/// </summary>
public sealed class OfficeAttachmentContextTests
{
    [Fact]
    public void 有sidecar时以markdown内联并给出路径()
    {
        using var temp = new TempDirectory();
        var ppt = Path.Combine(temp.Root, "需求评审.pptx");
        File.WriteAllBytes(ppt, [0x50, 0x4B, 0x03, 0x04]);
        var sidecar = ppt + ".office.md";
        File.WriteAllText(sidecar, "# 需求评审\n\n## 第 1 页 · 背景\n- 现状");

        var text = Map("总结这份 PPT 的需求", new ChatAttachment
        {
            FileName = "需求评审.pptx",
            FilePath = ppt,
            Size = 2048,
            ExtractedTextPath = sidecar,
            HasTextLayer = true
        });

        Assert.Contains("```markdown", text);
        Assert.Contains("## 第 1 页 · 背景", text);
        Assert.Contains("office_read", text);
        Assert.Contains(ppt, text);
        Assert.DoesNotContain("二进制或无法读取", text);
    }

    [Fact]
    public void 提取失败时提示可用office_read读取()
    {
        using var temp = new TempDirectory();
        var docx = Path.Combine(temp.Root, "需求.docx");
        File.WriteAllBytes(docx, [0x50, 0x4B, 0x03, 0x04]);

        var text = Map("看看这个文档", new ChatAttachment
        {
            FileName = "需求.docx",
            FilePath = docx,
            Size = 4096
        });

        Assert.Contains("office_read", text);
        Assert.Contains(docx, text);
        Assert.DoesNotContain("二进制或无法读取", text);
    }

    private static string Map(string content, ChatAttachment attachment)
    {
        var message = new ChatMessage(ChatMessageRole.User, content)
        {
            Attachments = [attachment]
        };

        return AgentMessageMapper.ToRequestMessage(message).Text;
    }
}
