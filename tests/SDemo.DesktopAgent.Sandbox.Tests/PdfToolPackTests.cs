using System.IO;
using SDemo.DesktopAgent.Platform.Windows.Tools;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S5 PDF 工具：路径 jail（工作区 + 会话附件目录）、大小 / 扩展名 / 符号链接拒绝、
/// 沙箱不可用时的文案，以及成功结果的格式化与审计。
/// </summary>
public sealed class PdfToolPackTests
{
    [Fact]
    public async Task 读取工作区里的PDF并传入页范围()
    {
        using var temp = new TempDirectory();
        var pdf = PdfFixture.CreateFile(temp.Root, "doc.pdf", "one", "two", "three");
        var harness = new Harness(temp.Root);
        harness.Jobs.Result = new PdfExtraction(
            true, 3, false, true, [new PdfPageText(1, "one"), new PdfPageText(2, "two")], false, null);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "doc.pdf", "1-2", 8000);

        Assert.Contains("PDF: doc.pdf", text);
        Assert.Contains("页数: 3", text);
        Assert.Contains("one", text);
        Assert.Contains("two", text);
        var call = Assert.Single(harness.Jobs.PdfCalls);
        Assert.Equal(Path.GetFullPath(pdf), call.Path);
        Assert.Equal("1-2", call.Pages);
        Assert.Equal(8000, call.MaxChars);
    }

    [Fact]
    public async Task 相对路径可以解析到会话附件目录()
    {
        using var temp = new TempDirectory();
        var attachments = Path.Combine(temp.Root, "uploads", "session-1");
        PdfFixture.CreateFile(attachments, "attached.pdf", "attachment text");

        var harness = new Harness(temp.Root);
        harness.ReadOnlyRoots.Add(attachments);
        harness.Jobs.Result = new PdfExtraction(
            true, 1, false, true, [new PdfPageText(1, "attachment text")], false, null);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "attached.pdf");

        Assert.Contains("attachment text", text);
        Assert.Single(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 绝对路径在允许根内可以读取()
    {
        using var temp = new TempDirectory();
        var attachments = Path.Combine(temp.Root, "uploads", "session-1");
        var pdf = PdfFixture.CreateFile(attachments, "attached.pdf", "text");

        var harness = new Harness(temp.Root);
        harness.ReadOnlyRoots.Add(attachments);
        harness.Jobs.Result = new PdfExtraction(true, 1, false, true, [new PdfPageText(1, "text")], false, null);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), pdf);

        Assert.Contains("text", text);
        Assert.Equal(Path.GetFullPath(pdf), Assert.Single(harness.Jobs.PdfCalls).Path);
    }

    [Fact]
    public async Task 绝对路径在工作区外时拒绝()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var pdf = PdfFixture.CreateFile(outside.Root, "secret.pdf", "secret");

        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), pdf);

        Assert.StartsWith("ERROR: PDF 路径不在允许", text);
        Assert.Empty(harness.Jobs.PdfCalls);
        Assert.Single(harness.Audit.Entries);
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("..\\outside.pdf")]
    [InlineData(@"\\server\share\a.pdf")]
    [InlineData(@"\\.\pipe\a.pdf")]
    public async Task 拒绝逃逸与UNC路径(string path)
    {
        using var temp = new TempDirectory();
        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), path);

        Assert.StartsWith("ERROR:", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 拒绝非PDF扩展名()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "notes.txt"), "text");
        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "notes.txt");

        Assert.Contains("只支持 .pdf", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 文件不存在时给出明确错误()
    {
        using var temp = new TempDirectory();
        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "missing.pdf");

        Assert.Contains("不存在", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 空PDF文件被拒绝()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Root, "empty.pdf"), []);
        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "empty.pdf");

        Assert.Contains("为空", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 工作区PDF超过50MB时拒绝()
    {
        using var temp = new TempDirectory();
        using (var stream = new FileStream(Path.Combine(temp.Root, "huge.pdf"), FileMode.CreateNew))
        {
            stream.SetLength(PdfLimits.MaxToolFileBytes + 1);
        }

        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "huge.pdf");

        Assert.Contains("过大", text);
        Assert.Contains("50 MB", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 附件目录PDF沿用25MB上限()
    {
        using var temp = new TempDirectory();
        var attachments = Path.Combine(temp.Root, "uploads", "session-1");
        Directory.CreateDirectory(attachments);
        using (var stream = new FileStream(Path.Combine(attachments, "huge.pdf"), FileMode.CreateNew))
        {
            stream.SetLength(PdfLimits.MaxAttachmentBytes + 1);
        }

        var harness = new Harness(temp.Root);
        harness.ReadOnlyRoots.Add(attachments);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "huge.pdf");

        Assert.Contains("过大", text);
        Assert.Contains("25 MB", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task 拒绝符号链接()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var target = PdfFixture.CreateFile(outside.Root, "outside.pdf", "secret");
        var link = Path.Combine(temp.Root, "link.pdf");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 标准用户默认没有创建符号链接的权限：跳过用例，不把环境限制当成产品缺陷。
            return;
        }

        var harness = new Harness(temp.Root);

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "link.pdf");

        Assert.Contains("符号链接", text);
        Assert.Empty(harness.Jobs.PdfCalls);
    }

    [Fact]
    public async Task pdf_info传递默认字符上限且不传页范围()
    {
        using var temp = new TempDirectory();
        PdfFixture.CreateFile(temp.Root, "info.pdf", "hello");
        var harness = new Harness(temp.Root);
        harness.Jobs.Result = new PdfExtraction(true, 1, false, true, [new PdfPageText(1, "hello")], false, null);

        var text = await harness.Pack.PdfInfoAsync(harness.Context(), "info.pdf");

        Assert.Contains("页数: 1", text);
        Assert.DoesNotContain("--- page", text);
        var call = Assert.Single(harness.Jobs.PdfCalls);
        Assert.Null(call.Pages);
        Assert.Equal(PdfLimits.DefaultChars, call.MaxChars);
    }

    [Fact]
    public async Task 沙箱不可用时原样返回错误()
    {
        using var temp = new TempDirectory();
        PdfFixture.CreateFile(temp.Root, "doc.pdf", "x");
        var harness = new Harness(temp.Root);
        harness.Jobs.Result = new PdfExtraction(
            false, 0, false, false, [], false, "ERROR: 执行沙箱已在设置中关闭。");

        var text = await harness.Pack.PdfReadAsync(harness.Context(), "doc.pdf");

        Assert.Equal("ERROR: 执行沙箱已在设置中关闭。", text);
        var audit = Assert.Single(harness.Audit.Entries);
        Assert.Equal(PdfToolPack.ReadFunctionId, audit.ToolId);
        Assert.Contains("关闭", audit.Error);
        Assert.Equal("NotRequired", audit.Approval);
    }

    [Fact]
    public async Task 工具包注册为Windows只读工具且默认启用()
    {
        var pack = new PdfToolPack();

        Assert.Equal("pdf", pack.Descriptor.Id);
        Assert.True(pack.Descriptor.WindowsOnly);
        Assert.True(pack.Descriptor.DefaultEnabled);
        Assert.True(pack.Descriptor.BuiltIn);
        Assert.Equal(AgentToolRisk.Low, pack.Descriptor.Risk);
        Assert.Equal(2, pack.Descriptor.Functions.Count);
        Assert.All(pack.Descriptor.Functions, function => Assert.False(function.RequiresApproval));
    }

    private sealed class Harness
    {
        private readonly ToolContext _context;

        public Harness(string workspaceRoot)
        {
            Settings = new TestSettingsService();
            Jobs = new FakeSandboxJobService();
            Audit = new RecordingAuditLog();
            _context = new ToolContext(
                "session-1",
                workspaceRoot,
                ReadOnlyRoots,
                Settings,
                new NoopApprovals(),
                Jobs,
                Audit);
        }

        public PdfToolPack Pack { get; } = new();

        public TestSettingsService Settings { get; }

        public FakeSandboxJobService Jobs { get; }

        public RecordingAuditLog Audit { get; }

        public List<string> ReadOnlyRoots { get; } = [];

        public ToolContext Context() => _context with { ReadOnlyRoots = ReadOnlyRoots.ToArray() };
    }

    private sealed class NoopApprovals : IToolApprovalService
    {
        public ToolApprovalRequest? Current => null;

#pragma warning disable CS0067
        public event Action? Changed;
#pragma warning restore CS0067

        public Task<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolApprovalDecision.ApproveOnce);

        public void Resolve(ToolApprovalDecision decision)
        {
        }

        public void CancelAll()
        {
        }
    }

    private sealed class RecordingAuditLog : IToolAuditLog
    {
        public List<ToolAuditEntry> Entries { get; } = [];

        public void Write(ToolAuditEntry entry) => Entries.Add(entry);
    }
}
