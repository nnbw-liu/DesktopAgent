using System.Security.Cryptography;
using System.Text;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P4：预览只复制到暂存区、注入 CSP，不碰工作区原件。</summary>
public sealed class ArtifactPreviewServiceTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _workspace;
    private readonly string _staging;
    private readonly TestPreviewHost _host;

    public ArtifactPreviewServiceTests()
    {
        _workspace = Path.Combine(_root.Root, "workspace");
        _staging = Path.Combine(_root.Root, "staging");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_staging);
        _host = new TestPreviewHost(_staging);
    }

    [Fact]
    public async Task 准备隔离预览注入_CSP_并保留原件()
    {
        var artifact = Path.Combine(_workspace, ".archify", "architecture-order", "order.html");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(
            artifact,
            "<!doctype html><html><head><title>订单</title></head><body><h1>ok</h1></body></html>");
        var originalHash = Hash(File.ReadAllBytes(artifact));

        var service = new ArtifactPreviewService(_host);
        var result = await service.PrepareAsync(
            _workspace,
            ".archify/architecture-order/order.html");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.PreviewUri);
        Assert.Equal("archify-preview.test", result.PreviewUri!.Host);
        Assert.Equal(originalHash, result.OriginalSha256);
        Assert.StartsWith("https://archify-preview.test/", result.PreviewUri.ToString());
        Assert.EndsWith("/order.html", result.PreviewUri.ToString());
        Assert.True(result.CspApplied);
        Assert.True(Directory.Exists(result.StagedRoot));

        var staged = Path.Combine(result.StagedRoot!, "order.html");
        Assert.True(File.Exists(staged));
        Assert.Contains("Content-Security-Policy", File.ReadAllText(staged));
        Assert.DoesNotContain("Content-Security-Policy", File.ReadAllText(artifact));
        Assert.Equal(originalHash, Hash(File.ReadAllBytes(artifact)));
    }

    [Fact]
    public async Task 复制同目录引用文件且不复制重解析点()
    {
        var folder = Path.Combine(_workspace, ".archify", "flow");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "flow.html"), "<html><head></head><body></body></html>");
        File.WriteAllText(Path.Combine(folder, "candidate.json"), "{}");

        var result = await new ArtifactPreviewService(_host)
            .PrepareAsync(_workspace, ".archify/flow/flow.html");

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(result.StagedRoot!, "candidate.json")));
    }

    [Fact]
    public async Task 已有_CSP_时不重复注入()
    {
        var artifact = Path.Combine(_workspace, ".archify", "erd", "erd.html");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(
            artifact,
            "<html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'\"></head></html>");

        var result = await new ArtifactPreviewService(_host)
            .PrepareAsync(_workspace, ".archify/erd/erd.html");

        Assert.True(result.Success, result.Message);
        Assert.False(result.CspApplied);
        Assert.Contains("default-src 'none'", File.ReadAllText(Path.Combine(result.StagedRoot!, "erd.html")));
    }

    [Theory]
    [InlineData("../secret.html")]
    [InlineData("C:/work/.archify/x/x.html")]
    [InlineData(".archify/../secret.html")]
    [InlineData("other/order.html")]
    public async Task 拒绝越界或非_archify_路径(string relative)
    {
        var result = await new ArtifactPreviewService(_host).PrepareAsync(_workspace, relative);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task 不支持预览的宿主给浏览器回退提示()
    {
        var service = new ArtifactPreviewService(new UnavailableArtifactPreviewHost());

        var result = await service.PrepareAsync(
            _workspace,
            ".archify/architecture-order/order.html");

        Assert.False(result.Success);
        Assert.Contains("用浏览器打开", result.Message);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class TestPreviewHost(string staging) : IArtifactPreviewHost
    {
        public bool IsSupported => true;

        public Uri? BaseUri => new("https://archify-preview.test/");

        public string? StagingRoot => staging;

        public string? ExportsRoot => Path.Combine(staging, "exports");
    }

    public void Dispose() => _root.Dispose();
}
