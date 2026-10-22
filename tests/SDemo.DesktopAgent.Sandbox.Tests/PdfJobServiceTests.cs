using System.IO;
using SDemo.DesktopAgent.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S5 端到端：PDF 复制到 job/input，由 AppContainer 内的 SandboxHost 解析，宿主读回 pdf.json。
/// 真实沙箱不可用的机器上这些用例会失败，和 S1–S4 的集成测试一样只在 Windows 跑。
/// </summary>
public sealed class PdfJobServiceTests
{
    [Fact]
    public async Task 端到端读取指定页()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var pdf = PdfFixture.CreateFile(temp.Root, "report.pdf", "Page one", "Page two", "Page three");

        var extraction = await host.Jobs.ExtractPdfAsync(pdf, pages: "2-3", maxChars: 20_000);

        Assert.True(extraction.Ok, extraction.Message);
        Assert.Equal(3, extraction.PageCount);
        Assert.False(extraction.Encrypted);
        Assert.True(extraction.HasTextLayer);
        Assert.Equal([2, 3], extraction.Pages.Select(page => page.Number).ToArray());
        Assert.Contains("Page two", extraction.Pages[0].Text);
        Assert.Contains("Page three", extraction.Pages[1].Text);
        Assert.Empty(JobDirectories(host));
    }

    [Fact]
    public async Task 默认读取前5页()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var pdf = PdfFixture.CreateFile(
            temp.Root,
            "many.pdf",
            Enumerable.Range(1, 7).Select(index => $"page {index}").ToArray());

        var extraction = await host.Jobs.ExtractPdfAsync(pdf, pages: null, maxChars: 20_000);

        Assert.True(extraction.Ok, extraction.Message);
        Assert.Equal(7, extraction.PageCount);
        Assert.Equal([1, 2, 3, 4, 5], extraction.Pages.Select(page => page.Number).ToArray());
    }

    [Fact]
    public async Task 页范围越界时返回明确错误且不保留job目录()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var pdf = PdfFixture.CreateFile(temp.Root, "short.pdf", "only page");

        var extraction = await host.Jobs.ExtractPdfAsync(pdf, pages: "9", maxChars: 20_000);

        Assert.False(extraction.Ok);
        Assert.Contains("页范围", extraction.Message);
        Assert.Empty(extraction.Pages);
        Assert.Empty(JobDirectories(host));
    }

    [Fact]
    public async Task 加密PDF返回Encrypted且不抛异常()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "encrypted.pdf");
        File.WriteAllBytes(pdf, PdfFixture.CreateEncrypted());

        var extraction = await host.Jobs.ExtractPdfAsync(pdf, pages: "1", maxChars: 20_000);

        Assert.False(extraction.Ok);
        Assert.True(extraction.Encrypted);
        Assert.Contains("加密", extraction.Message);
        Assert.Empty(JobDirectories(host));
    }

    [Fact]
    public async Task 损坏PDF返回解析失败()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Root, "broken.pdf");
        File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46, 0x00, 0x01, 0x02]);

        var extraction = await host.Jobs.ExtractPdfAsync(pdf, pages: "1", maxChars: 20_000);

        Assert.False(extraction.Ok);
        Assert.Contains("PDF 解析失败", extraction.Message);
        Assert.Empty(JobDirectories(host));
    }

    [Fact]
    public async Task 沙箱关闭时拒绝且不启动进程()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var pdf = PdfFixture.CreateFile(temp.Root, "report.pdf", "text");
        host.Settings.Current.Sandbox.Enabled = false;

        var extraction = await host.Jobs.ExtractPdfAsync(pdf, pages: "1", maxChars: 20_000);

        Assert.False(extraction.Ok);
        Assert.Contains("关闭", extraction.Message);
        Assert.Empty(JobDirectories(host));
    }

    [Fact]
    public async Task 非PDF扩展名与不存在的文件都被拒绝()
    {
        using var host = new SandboxTestHost();
        using var temp = new TempDirectory();
        var txt = Path.Combine(temp.Root, "notes.txt");
        File.WriteAllText(txt, "text");

        var wrongExtension = await host.Jobs.ExtractPdfAsync(txt, pages: "1", maxChars: 20_000);
        Assert.False(wrongExtension.Ok);
        Assert.Contains("只支持 .pdf", wrongExtension.Message);

        var missing = await host.Jobs.ExtractPdfAsync(
            Path.Combine(temp.Root, "missing.pdf"), pages: "1", maxChars: 20_000);
        Assert.False(missing.Ok);
        Assert.Contains("不存在", missing.Message);

        Assert.Empty(JobDirectories(host));
    }

    /// <summary>没有创建过任何 job 时 jobs 目录可能还不存在：这同样等价于“没有残留”。</summary>
    private static IReadOnlyList<string> JobDirectories(SandboxTestHost host) =>
        Directory.Exists(host.SandboxDirectory.JobsDirectory)
            ? Directory.GetDirectories(host.SandboxDirectory.JobsDirectory)
            : [];
}

