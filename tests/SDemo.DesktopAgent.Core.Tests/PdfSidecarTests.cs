using System.IO;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>PDF 文本 sidecar：路径可推导、UTF-8 无 BOM、写失败不抛出。</summary>
public sealed class PdfSidecarTests
{
    [Fact]
    public void 路径与PDF同目录同名只多txt后缀()
    {
        Assert.Equal(
            Path.Combine("data", "uploads", "a.pdf") + ".txt",
            PdfSidecar.PathFor(Path.Combine("data", "uploads", "a.pdf")));
    }

    [Fact]
    public async Task 写入成功返回路径且内容为UTF8无BOM()
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "a.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

        var path = await PdfSidecar.WriteAsync(pdf, new PdfAttachmentText("--- page 1 ---\n中文", 3, true));

        Assert.NotNull(path);
        Assert.Equal(pdf + ".txt", path);
        Assert.True(File.Exists(path));

        var bytes = File.ReadAllBytes(path!);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("中文", File.ReadAllText(path!));
    }

    [Fact]
    public async Task 目标目录不存在时返回null不抛出()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.pdf");

        var path = await PdfSidecar.WriteAsync(missing, new PdfAttachmentText("text", 1, true));

        Assert.Null(path);
    }
}
