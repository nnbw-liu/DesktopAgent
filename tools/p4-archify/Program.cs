using System.Text.Json;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Services;

// P4 验收探针：
// 1) 把 .archify 里的产物复制到暂存区、注入 CSP；
// 2) 用真实 Edge 跑 visual-check，检查 4 张截图与 contact sheet；
// 3) 输出 p4-probe-report.json。
//
// 用法：dotnet run --project tools/p4-archify -c Release --no-build -- ^
//   --skills-root .tmp/p3-skills --workspace .tmp/p3-workspace ^
//   --artifact .archify/architecture-p4/p4.html --staging .tmp/p4-preview

try
{
    var options = ProbeOptions.Parse(args);
    var root = Path.GetFullPath(Environment.CurrentDirectory);
    var skillsRoot = Path.GetFullPath(options.SkillsRoot ?? Path.Combine(root, ".tmp", "p3-skills"));
    var workspace = Path.GetFullPath(options.Workspace ?? Path.Combine(root, ".tmp", "p3-workspace"));
    var artifactInput = options.Artifact ?? Path.Combine(
        ".archify", "architecture-p4", "p4.html");
    var artifact = Path.GetFullPath(Path.Combine(workspace, artifactInput));
    var staging = Path.GetFullPath(options.Staging ?? Path.Combine(root, ".tmp", "p4-preview"));

    if (!File.Exists(artifact))
    {
        Console.Error.WriteLine($"产物不存在：{artifact}");
        return 1;
    }

    var relative = Path.GetRelativePath(workspace, artifact).Replace('\\', '/');
    var host = new ProbePreviewHost(staging);
    var preview = await new ArtifactPreviewService(host).PrepareAsync(workspace, relative);

    if (!preview.Success)
    {
        Console.Error.WriteLine($"预览准备失败：{preview.Message}");
        return 1;
    }

    var stagedHtml = Path.Combine(preview.StagedRoot!, Path.GetFileName(artifact));
    var stagedText = File.ReadAllText(stagedHtml);
    Require(stagedText.Contains("Content-Security-Policy", StringComparison.Ordinal), "暂存副本没有 CSP。");
    Require(preview.PreviewUri!.Host == "archify-preview.test", "预览 URI 的源不对。");

    var catalog = new SkillCatalog(skillsRoot);
    var settings = new ProbeSettings();
    var audit = new ProbeAudit();
    var runner = new SkillScriptRunner(new DirectProcessRunner());
    var review = await new ArchifyReviewService(catalog, runner, settings, audit)
        .ReviewAsync(workspace, relative);

    if (!review.Success)
    {
        Console.Error.WriteLine($"视觉复核失败：{review.Message}");
        return 1;
    }

    Require(review.Status == "pass", $"视觉复核 status 不是 pass：{review.Status}");
    Require(review.VisualReview == "pending", $"visualReview 不是 pending：{review.VisualReview}");
    Require(review.Screenshots is { Count: 4 }, "截图不是 4 张。");
    Require(review.ContactSheet is not null && File.Exists(review.ContactSheet), "联系表不存在。");
    Require(audit.Entries.Count == 1, "视觉复核没有写审计。");

    foreach (var shot in review.Screenshots!)
    {
        Require(File.Exists(shot.Path), $"截图不存在：{shot.Path}");
    }

    var report = new
    {
        probe = "archify-p4",
        timestamp = DateTimeOffset.Now.ToString("O"),
        workspace,
        artifact,
        artifactSha256 = preview.OriginalSha256,
        preview = new
        {
            uri = preview.PreviewUri.ToString(),
            stagedRoot = preview.StagedRoot,
            cspApplied = preview.CspApplied,
            sandbox = "allow-scripts allow-downloads",
            sameOrigin = false
        },
        review = new
        {
            review.Status,
            review.VisualReview,
            review.ContactSheet,
            review.Receipt,
            review.OutputDirectory,
            screenshots = review.Screenshots
        },
        audit = audit.Entries.Select(entry => new
        {
            entry.ToolId,
            entry.Mode,
            entry.ExitCode,
            entry.ScriptSha256,
            entry.CommandSha256,
            entry.DurationMs
        }),
        pass = true
    };

    var reportPath = Path.Combine(Path.GetDirectoryName(artifact)!, "p4-probe-report.json");
    File.WriteAllText(
        reportPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

    Console.WriteLine($"预览源：{preview.PreviewUri}");
    Console.WriteLine($"暂存：{preview.StagedRoot}（CSP {preview.CspApplied}）");
    Console.WriteLine($"复核：{review.Status} / visualReview={review.VisualReview} / 截图 {review.Screenshots.Count} 张");
    Console.WriteLine($"联系表：{review.ContactSheet}");
    Console.WriteLine($"审计：{audit.Entries.Count} 条；脚本 {audit.Entries[0].ScriptSha256}");
    Console.WriteLine($"报告：{reportPath}");
    Console.WriteLine("P4 预览与复核检查通过。");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"P4 检查失败：{ex.Message}");
    Console.Error.WriteLine(ex);
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record ProbeOptions(string? SkillsRoot, string? Workspace, string? Artifact, string? Staging)
{
    public static ProbeOptions Parse(string[] args)
    {
        string? Read(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new ProbeOptions(
            Read("--skills-root"),
            Read("--workspace"),
            Read("--artifact"),
            Read("--staging"));
    }
}

internal sealed class ProbePreviewHost(string staging) : IArtifactPreviewHost
{
    public bool IsSupported => true;

    public Uri? BaseUri => new("https://archify-preview.test/");

    public string? StagingRoot => staging;

    public string? ExportsRoot => Path.Combine(staging, "exports");
}

internal sealed class ProbeSettings : ISettingsService
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

internal sealed class ProbeAudit : IToolAuditLog
{
    public List<ToolAuditEntry> Entries { get; } = [];

    public void Write(ToolAuditEntry entry) => Entries.Add(entry);
}
