using System.Text.Json;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P4：视觉复核只走宿主显式触发，参数限定 .archify，并如实报告 pending。</summary>
public sealed class ArchifyReviewServiceTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _workspace;
    private readonly FakeSettings _settings = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeSkillLibrary _library;

    public ArchifyReviewServiceTests()
    {
        _workspace = Path.Combine(_root.Root, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, ".archify", "flow"));
        File.WriteAllText(
            Path.Combine(_workspace, ".archify", "flow", "flow.html"),
            "<html><head></head><body></body></html>");
        _library = new FakeSkillLibrary(new SkillScriptTarget(
            "archify",
            "bin/archify.mjs",
            Path.Combine(_root.Root, "archify.mjs"),
            new string('a', 64),
            123,
            Path.Combine(_root.Root, "archify"),
            "https://github.com/tt-a1i/archify"));
    }

    [Fact]
    public async Task 运行_visual_check_并解析截图与_pending()
    {
        var service = new ArchifyReviewService(_library, _runner, _settings, _audit);

        var result = await service.ReviewAsync(_workspace, ".archify/flow/flow.html");

        Assert.True(result.Success, result.Message);
        Assert.Equal("pass", result.Status);
        Assert.Equal("pending", result.VisualReview);
        Assert.Contains("visualReview=pending", result.Message);
        Assert.Contains("自动截图不等于人工视觉复核", result.Message);
        Assert.Single(result.Screenshots!);
        Assert.Equal(1440, result.Screenshots![0].Width);
        Assert.EndsWith("visual-check", result.OutputDirectory);
        Assert.Equal(".archify/flow/flow.html", _runner.LastRequest!.Arguments[1]);
        Assert.Equal(".archify/flow/visual-check", _runner.LastRequest.Arguments[5]);
        Assert.Equal(Path.GetFullPath(_workspace), _runner.LastRequest.WorkingDirectory);
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal("archify_visual_check", audit.ToolId);
        Assert.Equal("direct", audit.Mode);
        Assert.Equal(0, audit.ExitCode);
        Assert.StartsWith("sha256:", audit.ScriptSha256);
    }

    [Fact]
    public async Task 直跑未开启时拒绝且不启动进程()
    {
        _settings.Current.Execution.DirectRunEnabled = false;
        var service = new ArchifyReviewService(_library, _runner, _settings, _audit);

        var result = await service.ReviewAsync(_workspace, ".archify/flow/flow.html");

        Assert.False(result.Success);
        Assert.Contains("直跑", result.Message);
        Assert.Null(_runner.LastRequest);
    }

    [Fact]
    public async Task 越界路径被拒绝()
    {
        var result = await new ArchifyReviewService(_library, _runner, _settings, _audit)
            .ReviewAsync(_workspace, "../flow.html");

        Assert.False(result.Success);
        Assert.Null(_runner.LastRequest);
    }

    [Fact]
    public async Task 非_JSON_输出给可恢复失败()
    {
        _runner.Result = _runner.Result with { StdOut = "not json" };
        var service = new ArchifyReviewService(_library, _runner, _settings, _audit);

        var result = await service.ReviewAsync(_workspace, ".archify/flow/flow.html");

        Assert.False(result.Success);
        Assert.Contains("没有返回可解析的 JSON", result.Message);
    }

    private sealed class FakeSkillLibrary(SkillScriptTarget target) : ISkillLibrary
    {
        public IReadOnlyList<SkillIndexEntry> Index() => [];

        public string? ReadContent(string? name) => null;

        public SkillFileReadResult ReadFile(string? name, string? path) =>
            SkillFileReadResult.Fail(SkillFileStatus.SkillNotFound);

        public SkillScriptResolution ResolveScript(string? name, string? script) =>
            name == "archify" && script == "bin/archify.mjs"
                ? SkillScriptResolution.Ok(target)
                : SkillScriptResolution.Fail(SkillScriptStatus.SkillNotFound);
    }

    private sealed class FakeRunner : ISkillScriptRunner
    {
        public SkillScriptHostInfo Host { get; } =
            new(true, "node", "20.11.0", "edge.exe", "ok");

        public SkillScriptRequest? LastRequest { get; private set; }

        public SkillScriptRunResult Result { get; set; } = new(
            true,
            "node",
            "20.11.0",
            0,
            JsonSerializer.Serialize(new
            {
                ok = true,
                status = "pass",
                visualReview = "pending",
                checks = new { containment = "pass" },
                evidence = new
                {
                    contactSheet = "C:\\work\\visual-check\\flow.visual-check.html",
                    receipt = "C:\\work\\visual-check\\flow.visual-check.json",
                    screenshots = new[]
                    {
                        new
                        {
                            path = "C:\\work\\visual-check\\flow.1440x900.light.png",
                            width = 1440,
                            height = 900,
                            theme = "light",
                            resolvedTheme = "light"
                        }
                    }
                }
            }),
            string.Empty,
            false,
            false,
            1200,
            false,
            false);

        public Task<SkillScriptRunResult> RunAsync(
            SkillScriptRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Execution = new ExecutionSettings
            {
                Mode = ExecutionMode.AllowlistDirect,
                DirectRunEnabled = true,
                Allowlist = ["node"],
                DefaultTimeoutSeconds = 600,
                BuildTimeoutSeconds = 600
            }
        };

        public event EventHandler? Changed;

        public void Save() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeAudit : IToolAuditLog
    {
        public List<ToolAuditEntry> Entries { get; } = [];

        public void Write(ToolAuditEntry entry) => Entries.Add(entry);
    }

    public void Dispose() => _root.Dispose();
}
