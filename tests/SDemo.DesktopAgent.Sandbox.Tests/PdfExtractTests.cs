using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S5 PDF helper：直接运行 SandboxHost（不经过 AppContainer），验证 pdf.json 协议、
/// 页范围、截断、损坏 / 加密文件与路径逃逸。解析器崩溃隔离由真实沙箱用例覆盖。
/// </summary>
public sealed class PdfExtractTests
{
    private static readonly string HostPath = Path.Combine(
        AppContext.BaseDirectory,
        "sandbox-host",
        "SDemo.DesktopAgent.SandboxHost.exe");

    [Fact]
    public void SandboxHost已随测试输出复制()
    {
        Assert.True(File.Exists(HostPath), $"找不到 {HostPath}");
    }

    [Fact]
    public async Task 读取指定页并返回页数与文本()
    {
        using var job = new PdfJob(PdfFixture.Create("Page one text", "Page two text", "Page three text"));
        job.WriteJobJson(pages: "2-3");

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.True(pdf.Ok, pdf.Message);
        Assert.Equal(3, pdf.PageCount);
        Assert.False(pdf.Encrypted);
        Assert.True(pdf.HasTextLayer);
        Assert.Equal([2, 3], pdf.Pages.Select(page => page.Number).ToArray());
        Assert.Contains("Page two text", pdf.Pages[0].Text);
        Assert.Contains("Page three text", pdf.Pages[1].Text);
        Assert.False(pdf.Truncated);
        Assert.False(File.Exists(Path.Combine(job.Output, "pdf.json.tmp")));
    }

    [Fact]
    public async Task 默认只读前5页()
    {
        using var job = new PdfJob(PdfFixture.Create(
            "p1", "p2", "p3", "p4", "p5", "p6", "p7"));
        job.WriteJobJson(pages: null);

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.True(pdf.Ok, pdf.Message);
        Assert.Equal(7, pdf.PageCount);
        Assert.Equal([1, 2, 3, 4, 5], pdf.Pages.Select(page => page.Number).ToArray());
    }

    [Theory]
    [InlineData("9")]
    [InlineData("1-9")]
    [InlineData("0")]
    [InlineData("3-1")]
    [InlineData("a")]
    [InlineData("1-2-3")]
    public async Task 页范围非法时返回解析失败(string pages)
    {
        using var job = new PdfJob(PdfFixture.Create("one", "two", "three"));
        job.WriteJobJson(pages: pages);

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.False(pdf.Ok);
        Assert.False(pdf.Encrypted);
        Assert.Contains("页范围", pdf.Message);
        Assert.Empty(pdf.Pages);
    }

    [Fact]
    public async Task 单次超过50页时拒绝()
    {
        using var job = new PdfJob(PdfFixture.Create(Enumerable.Range(1, 60).Select(index => $"p{index}").ToArray()));
        job.WriteJobJson(pages: "1-60", maxChars: 40_000);

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.False(pdf.Ok);
        Assert.Contains("50", pdf.Message);
    }

    [Fact]
    public async Task 字符超限时截断并标记()
    {
        using var job = new PdfJob(PdfFixture.Create(
            new string('a', 5000),
            new string('b', 5000)));
        job.WriteJobJson(pages: "1-2", maxChars: 1000);

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.True(pdf.Ok, pdf.Message);
        Assert.True(pdf.Truncated);
        Assert.Equal([1], pdf.Pages.Select(page => page.Number).ToArray());
        Assert.True(pdf.Pages[0].Text.Length <= 1000);
    }

    [Fact]
    public async Task 空白PDF标记无文本层()
    {
        using var job = new PdfJob(PdfFixture.CreateBlank(2));
        job.WriteJobJson(pages: "1-2");

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.True(pdf.Ok, pdf.Message);
        Assert.Equal(2, pdf.PageCount);
        Assert.False(pdf.HasTextLayer);
    }

    [Fact]
    public async Task 损坏PDF返回解析失败且不抛出()
    {
        using var job = new PdfJob([1, 2, 3, 4, 5, 6, 7, 8]);
        job.WriteJobJson(pages: "1");

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.False(pdf.Ok);
        Assert.Contains("PDF 解析失败", pdf.Message);
        // 解析失败也要写出 result.json，供宿主区分“helper 崩溃”与“文件损坏”。
        Assert.True(File.Exists(Path.Combine(job.Output, SandboxProtocol.ResultFileName)));
    }

    [Fact]
    public async Task 加密PDF标记Encrypted()
    {
        using var job = new PdfJob(PdfFixture.CreateEncrypted());
        job.WriteJobJson(pages: "1");

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.False(pdf.Ok);
        Assert.True(pdf.Encrypted, pdf.Message);
    }

    [Fact]
    public async Task PdfInfo返回页数与文本层()
    {
        using var job = new PdfJob(PdfFixture.Create("first", "second", "third", "fourth"));
        job.WriteJobJson(mode: SandboxJobMode.PdfInfo, pages: null);

        var run = await RunAsync(job);
        var pdf = ReadPdf(job);

        Assert.Equal(0, run.ExitCode);
        Assert.True(pdf.Ok, pdf.Message);
        Assert.Equal(4, pdf.PageCount);
        Assert.True(pdf.HasTextLayer);
        Assert.Equal([1, 2, 3], pdf.Pages.Select(page => page.Number).ToArray());
    }

    [Fact]
    public async Task 输入文件路径逃逸时拒绝()
    {
        using var job = new PdfJob(PdfFixture.Create("x"));
        job.WriteJobJson(inputFile: "../outside.pdf", pages: "1");

        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("拒绝", ReadResult(job).Error);
        Assert.False(File.Exists(Path.Combine(job.Output, SandboxProtocol.PdfResultFileName)));
    }

    [Fact]
    public async Task 缺少inputFile时按协议错误拒绝()
    {
        using var job = new PdfJob(PdfFixture.Create("x"));
        job.WriteJobJson(inputFile: null, pages: "1");

        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("inputFile", ReadResult(job).Error);
        Assert.False(File.Exists(Path.Combine(job.Output, SandboxProtocol.PdfResultFileName)));
    }

    [Fact]
    public async Task 文件不存在时给出明确错误()
    {
        using var job = new PdfJob(PdfFixture.Create("x"), copyInput: false);
        job.WriteJobJson(pages: "1");

        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("不存在", ReadResult(job).Error);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(PdfJob job)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath,
            WorkingDirectory = job.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(job.JobJsonPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 SandboxHost。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static PdfResultDocument ReadPdf(PdfJob job)
    {
        var path = Path.Combine(job.Output, SandboxProtocol.PdfResultFileName);
        Assert.True(File.Exists(path), $"找不到 {path}");

        return JsonSerializer.Deserialize<PdfResultDocument>(File.ReadAllText(path), SandboxJson.Options)
               ?? throw new InvalidOperationException("pdf.json 为空。");
    }

    private static SandboxResultDocument ReadResult(PdfJob job) =>
        JsonSerializer.Deserialize<SandboxResultDocument>(
            File.ReadAllText(Path.Combine(job.Output, SandboxProtocol.ResultFileName)),
            SandboxJson.Options)
        ?? throw new InvalidOperationException("result.json 为空。");

    private sealed class PdfJob : IDisposable
    {
        public PdfJob(byte[] pdf, bool copyInput = true)
        {
            Id = $"20260101-000000-{Guid.NewGuid():N}"[..22];
            Root = Path.Combine(Path.GetTempPath(), "sdemo-pdf-tests", Id);
            Input = Path.Combine(Root, "input");
            Work = Path.Combine(Root, "work");
            Output = Path.Combine(Root, "output");
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Work);
            Directory.CreateDirectory(Output);

            if (copyInput)
            {
                File.WriteAllBytes(Path.Combine(Input, SandboxProtocol.PdfInputFileName), pdf);
            }

            WriteJobJson();
        }

        public string Id { get; }

        public string Root { get; }

        public string Input { get; }

        public string Work { get; }

        public string Output { get; }

        public string JobJsonPath => Path.Combine(Input, SandboxProtocol.JobFileName);

        public void WriteJobJson(
            SandboxJobMode mode = SandboxJobMode.PdfExtract,
            string? inputFile = "input/document.pdf",
            string? pages = "1",
            int maxChars = 20_000)
        {
            var document = new SandboxJobDocument
            {
                JobId = Id,
                Mode = mode,
                InputFile = inputFile,
                Pages = pages,
                MaxChars = maxChars,
                WorkingDirectory = "work",
                TimeoutSeconds = 30,
                MaxOutputBytes = 1024 * 1024
            };

            File.WriteAllText(JobJsonPath, JsonSerializer.Serialize(document, SandboxJson.Options));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
