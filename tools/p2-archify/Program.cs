using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

// P2 真实验收探针：
// 1) 技能库从目录包解析 bin/archify.mjs；
// 2) 通过 run_skill_script 先跑一个坏候选，拿到 validate 失败证据；
// 3) 修复候选后重跑，拿到 pass + 交付 HTML；
// 4) 校验审批、会话授权、审计与产物哈希。
//
// 用法：dotnet run --project tools/p2-archify -c Release
// 可选：--skill-root <目录> --template <P0 candidate.json> --workspace <目录>

try
{
    var options = ProbeOptions.Parse(args);
    var root = Path.GetFullPath(Environment.CurrentDirectory);
    var skillRoot = Path.GetFullPath(options.SkillRoot ?? Path.Combine(root, ".tmp", "archify-repo"));
    var workspace = Path.GetFullPath(options.Workspace ?? Path.Combine(root, ".tmp", "p2-workspace"));
    var template = options.Template is null
        ? FindTemplate(root)
        : Path.GetFullPath(options.Template);

    Require(Directory.Exists(Path.Combine(skillRoot, "archify")), $"技能根目录缺少 archify 包：{skillRoot}");
    Require(File.Exists(template), $"缺少有效候选模板：{template}");

    if (Directory.Exists(workspace))
    {
        Directory.Delete(workspace, recursive: true);
    }

    Directory.CreateDirectory(workspace);

    var valid = JsonNode.Parse(File.ReadAllText(template))
        ?? throw new InvalidOperationException("候选模板不是合法 JSON。");
    valid["meta"] ??= new JsonObject();
    valid["meta"]!["title"] = "P2 DesktopAgent 验收图";
    valid["meta"]!["output"] = "p2.html";

    var validPath = Path.Combine(workspace, "candidate.json");
    File.WriteAllText(validPath, valid.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var broken = JsonNode.Parse(valid.ToJsonString())!;
    broken["meta"]!["title"] = "P2 坏候选（缺组件）";
    broken["components"] = new JsonArray();
    File.WriteAllText(validPath, broken.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var catalog = new SkillCatalog(skillRoot);
    var resolution = catalog.ResolveScript("archify", "bin/archify.mjs");
    Require(resolution.IsOk, $"解析脚本失败：{resolution.Status}");
    var target = resolution.Target!;

    var settings = new ProbeSettingsService();
    settings.Current.Execution.DirectRunEnabled = true;
    settings.Current.Execution.Mode = ExecutionMode.AllowlistDirect;

    var runner = new SkillScriptRunner(new DirectProcessRunner());
    var approvals = new ToolApprovalService();
    var grants = new SessionGrantStore();
    var audit = new RecordingAudit();
    var pack = new SkillToolPack(runner, grants);

    var context = new ToolContext(
        "p2-probe",
        workspace,
        [],
        settings,
        approvals,
        new UnavailableSandboxJobService(),
        audit,
        Skills: catalog);

    Console.WriteLine($"技能根：{skillRoot}");
    Console.WriteLine($"脚本：{target.RelativePath}（{target.Bytes} B，sha256 {target.Sha256}）");
    Console.WriteLine($"Node：{runner.Host.NodePath}（{runner.Host.NodeVersion}）");
    Console.WriteLine($"浏览器：{runner.Host.ChromePath ?? "未找到"}");
    Console.WriteLine($"工作区：{workspace}");

    var first = await RunAsync(pack, context, approvals, "out/review-1");
    Console.WriteLine($"第一次退出码：{first.ExitCode}；状态：{first.Receipt?["status"]}");
    Require(first.ExitCode != 0, "坏候选居然通过了 validate，验收前提不成立。");
    Require(string.Equals(first.Receipt?["gates"]?["validate"]?.ToString(), "fail", StringComparison.Ordinal),
        "坏候选没有在 validate 阶段失败。");
    Require(!File.Exists(Path.Combine(workspace, "p2.html")), "失败运行不应产出 HTML。");

    // 修复：把有效候选（含 components / relationships）写回同一个 candidate.json。
    File.WriteAllText(validPath, valid.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var second = await RunAsync(pack, context, approvals, "out/review-2");
    Console.WriteLine($"第二次退出码：{second.ExitCode}；状态：{second.Receipt?["status"]}");

    Require(second.ExitCode == 0, "修复后仍然失败。");
    Require(string.Equals(second.Receipt?["status"]?.ToString(), "pass", StringComparison.Ordinal),
        "修复后 receipt 不是 pass。");

    foreach (var gate in new[] { "validate", "deliver", "check", "browser-check" })
    {
        Require(string.Equals(second.Receipt?["gates"]?[gate]?.ToString(), "pass", StringComparison.Ordinal),
            $"gate {gate} 未通过。");
    }

    var artifactPath = Path.Combine(workspace, "p2.html");
    Require(File.Exists(artifactPath), "没有产出 p2.html。");

    var artifact = new FileInfo(artifactPath);
    Require(artifact.Length > 300_000, $"产物体积异常：{artifact.Length} B");

    var artifactText = File.ReadAllText(artifactPath);
    Require(artifactText.Contains("<svg", StringComparison.OrdinalIgnoreCase), "产物里没有内联 SVG。");

    var artifactSha = Sha256File(artifactPath);
    Require(string.Equals(second.Receipt?["artifact"]?["sha256"]?.ToString(), artifactSha, StringComparison.Ordinal),
        "receipt 里的 artifact sha256 与磁盘不一致。");

    Require(File.Exists(Path.Combine(workspace, "out", "review-2", "p2.browser-check.json")),
        "缺少 browser-check 证据。");

    var browserCheck = JsonNode.Parse(
        File.ReadAllText(Path.Combine(workspace, "out", "review-2", "p2.browser-check.json")));
    Require(browserCheck?["ok"]?.GetValue<bool>() == true, "browser-check 未通过。");

    Require(audit.Entries.Count == 2, $"审计条数应为 2，实际 {audit.Entries.Count}。");

    foreach (var entry in audit.Entries)
    {
        Require(string.Equals(entry.Mode, "direct", StringComparison.Ordinal), "审计 Mode 不是 direct。");
        Require(entry.ScriptSha256?.StartsWith("sha256:", StringComparison.Ordinal) == true, "审计缺少脚本哈希。");
        Require(entry.CommandSha256?.StartsWith("sha256:", StringComparison.Ordinal) == true, "审计缺少参数哈希。");
        Require(entry.WorkingDirectory == ".", "审计工作目录不是相对路径。");
    }

    Require(audit.Entries[0].Approval == "SessionGrant", "第一次运行没有记会话授权。");
    Require(audit.Entries[1].Approval == "SessionGrant", "第二次运行没有沿用会话授权。");

    var report = new
    {
        probe = "archify-p2",
        timestamp = DateTimeOffset.Now.ToString("O"),
        node = new { version = runner.Host.NodeVersion, path = runner.Host.NodePath },
        browser = runner.Host.ChromePath,
        skill = new
        {
            root = skillRoot,
            script = target.RelativePath,
            bytes = target.Bytes,
            sha256 = target.Sha256
        },
        firstRun = new
        {
            exitCode = first.ExitCode,
            status = first.Receipt?["status"]?.ToString(),
            failedStage = first.Receipt?["failedStage"]?.ToString(),
            diagnostic = first.Receipt?["diagnostics"]?[0]?["code"]?.ToString(),
            durationMs = first.Receipt?["durationMs"]?.GetValue<int>()
        },
        secondRun = new
        {
            exitCode = second.ExitCode,
            status = second.Receipt?["status"]?.ToString(),
            gates = second.Receipt?["gates"]?.ToJsonString(),
            durationMs = second.Receipt?["durationMs"]?.GetValue<int>()
        },
        approvals = new { requested = first.ApprovalRequests + second.ApprovalRequests, sessionGrant = grants.List("p2-probe").Count },
        artifact = new { path = artifactPath, bytes = artifact.Length, sha256 = artifactSha },
        audit = audit.Entries.Select(entry => new
        {
            scriptSha256 = entry.ScriptSha256,
            commandSha256 = entry.CommandSha256,
            workingDirectory = entry.WorkingDirectory,
            approval = entry.Approval,
            mode = entry.Mode,
            exitCode = entry.ExitCode,
            durationMs = entry.DurationMs,
            error = entry.Error
        }),
        pass = true
    };

    var reportPath = Path.Combine(workspace, "p2-probe-report.json");
    File.WriteAllText(
        reportPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

    Console.WriteLine($"P2 验收通过；产物 {artifact.Length} B，sha256 {artifactSha}");
    Console.WriteLine($"报告：{reportPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"P2 验收失败：{ex.Message}");
    Console.Error.WriteLine(ex);
    return 1;
}

static async Task<ToolRun> RunAsync(
    SkillToolPack pack,
    ToolContext context,
    ToolApprovalService approvals,
    string outDir)
{
    var tool = pack.Create(context).OfType<AIFunction>()
        .Single(item => item.Name == SkillToolPack.RunScriptFunctionId);

    var arguments = new Dictionary<string, object?>
    {
        ["skill"] = "archify",
        ["script"] = "bin/archify.mjs",
        ["args"] = new[]
        {
            "finalize", "architecture", "candidate.json", "p2.html",
            "--quality", "showcase", "--json", "--out-dir", outDir
        },
        ["timeoutSeconds"] = 300
    };

    var invocation = tool.InvokeAsync(new AIFunctionArguments(arguments)).AsTask();
    var seen = new HashSet<ToolApprovalRequest>(ReferenceEqualityComparer.Instance);
    var approvalRequests = 0;

    while (!invocation.IsCompleted)
    {
        var request = approvals.Current;

        if (request is not null && seen.Add(request))
        {
            approvalRequests++;
            Console.WriteLine(
                $"审批请求：{request.Title} | risk={request.Risk} | key={request.SessionKey}");
            approvals.Resolve(ToolApprovalDecision.ApproveForSession);
        }

        await Task.Delay(25);
    }

    var text = (await invocation)?.ToString() ?? string.Empty;
    var stdout = ExtractStdout(text);
    var receiptLine = stdout
        .Split('\n')
        .LastOrDefault(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal));

    JsonNode? receipt = null;

    if (!string.IsNullOrWhiteSpace(receiptLine))
    {
        try
        {
            receipt = JsonNode.Parse(receiptLine);
        }
        catch (JsonException)
        {
            // 失败输出可能不是 JSON；保留 text 供排查。
        }
    }

    var exitCode = int.TryParse(
        text.Split('\n').FirstOrDefault(line => line.StartsWith("退出码：", StringComparison.Ordinal))?
            ["退出码：".Length..].Split('（')[0],
        out var parsed)
        ? parsed
        : -999;

    Console.WriteLine("---- 工具结果 ----");
    Console.WriteLine(text.Length > 4000 ? text[..4000] + "\n…（截断显示）" : text);

    return new ToolRun(text, exitCode, stdout, receipt, approvalRequests);
}

static string ExtractStdout(string text)
{
    const string begin = "--- stdout ---";
    const string end = "--- stderr ---";
    var start = text.IndexOf(begin, StringComparison.Ordinal);

    if (start < 0)
    {
        return string.Empty;
    }

    start += begin.Length;
    var stop = text.IndexOf(end, start, StringComparison.Ordinal);

    return (stop < 0 ? text[start..] : text[start..stop]).Trim();
}

static string Sha256File(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static string FindTemplate(string root)
{
    var p0 = Path.Combine(root, ".tmp", "p0-workspace");

    if (!Directory.Exists(p0))
    {
        throw new InvalidOperationException($"找不到 P0 候选目录：{p0}");
    }

    return Directory.EnumerateFiles(p0, "candidate.json", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault()
        ?? throw new InvalidOperationException("P0 目录里没有 candidate.json。");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record ToolRun(string Text, int ExitCode, string Stdout, JsonNode? Receipt, int ApprovalRequests);

internal sealed record ProbeOptions(string? SkillRoot, string? Template, string? Workspace)
{
    public static ProbeOptions Parse(string[] args)
    {
        string? Read(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new ProbeOptions(
            Read("--skill-root"),
            Read("--template"),
            Read("--workspace"));
    }
}

internal sealed class ProbeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public void Save() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class RecordingAudit : IToolAuditLog
{
    public List<ToolAuditEntry> Entries { get; } = [];

    public void Write(ToolAuditEntry entry) => Entries.Add(entry);
}
