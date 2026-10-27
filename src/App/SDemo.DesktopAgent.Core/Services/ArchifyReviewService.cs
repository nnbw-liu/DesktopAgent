using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// P4 视觉复核：用户点击后跑 <c>archify.mjs visual-check --summary --require-provenance</c>。
/// 只做自动浏览器检查和截图；<c>visualReview</c> 仍是 pending 时不算人工复核。
/// </summary>
public sealed class ArchifyReviewService : IArchifyReviewService
{
    private const string SkillName = "archify";
    private const string ScriptPath = "bin/archify.mjs";
    private const int DefaultTimeoutSeconds = 600;

    private readonly ISkillLibrary _library;
    private readonly ISkillScriptRunner _runner;
    private readonly ISettingsService _settings;
    private readonly IToolAuditLog _audit;

    public ArchifyReviewService(
        ISkillLibrary library,
        ISkillScriptRunner runner,
        ISettingsService settings,
        IToolAuditLog audit)
    {
        _library = library;
        _runner = runner;
        _settings = settings;
        _audit = audit;
    }

    public async Task<ArchifyReviewResult> ReviewAsync(
        string workspaceRoot,
        string artifactRelativePath,
        CancellationToken cancellationToken = default)
    {
        string workspace;
        string? artifact;

        try
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return ArchifyReviewResult.Fail("当前会话没有工作区，无法做视觉复核。");
            }

            workspace = Path.GetFullPath(workspaceRoot);
            artifact = ResolveArtifact(workspace, artifactRelativePath);

            if (artifact is null)
            {
                return ArchifyReviewResult.Fail("只允许复核工作区 .archify 下的 HTML 产物。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return ArchifyReviewResult.Fail($"无法读取产物：{ex.Message}");
        }

        var resolution = _library.ResolveScript(SkillName, ScriptPath);

        if (!resolution.IsOk)
        {
            return ArchifyReviewResult.Fail(
                $"技能库里的 Archify 脚本不可用：{resolution.Status}。请先在技能设置里安装 Archify 3.2.0。");
        }

        var target = resolution.Target!;
        var artifactRelative = Path.GetRelativePath(workspace, artifact)
            .Replace('\\', '/');
        var outputRelative = Path.GetDirectoryName(artifactRelative)!
            .Replace('\\', '/') + "/visual-check";

        var arguments = new[]
        {
            "visual-check",
            artifactRelative,
            "--summary",
            "--require-provenance",
            "--out-dir",
            outputRelative
        };

        var execution = _settings.Current.Execution;
        var policy = SkillScriptPolicy.Evaluate(
            arguments,
            ".",
            DefaultTimeoutSeconds,
            workspace,
            execution);

        if (!policy.Allowed)
        {
            return ArchifyReviewResult.Fail(policy.Reason);
        }

        if (!_runner.Host.NodeAvailable)
        {
            return ArchifyReviewResult.Fail(
                string.IsNullOrWhiteSpace(_runner.Host.Detail)
                    ? "Node.js 不可用，无法执行 visual-check。"
                    : _runner.Host.Detail);
        }

        var request = new SkillScriptRequest(
            target.SkillName,
            target.Source,
            target.FullPath,
            target.RelativePath,
            target.Sha256,
            policy.Arguments,
            workspace,
            TimeSpan.FromSeconds(policy.TimeoutSeconds),
            Math.Clamp(execution.MaxOutputChars, 4096, 64_000));

        var commandHash = CommandHash(target.RelativePath, policy.Arguments);

        try
        {
            var run = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            var error = run.Error;
            var parsed = Parse(run.StdOut);

            WriteAudit(target, run, commandHash, error);

            if (!string.IsNullOrWhiteSpace(error))
            {
                return ArchifyReviewResult.Fail($"视觉复核运行失败：{error}");
            }

            if (run.TimedOut)
            {
                return ArchifyReviewResult.Fail("视觉复核超时；产物未改动。");
            }

            if (run.Canceled)
            {
                return ArchifyReviewResult.Fail("视觉复核已取消。");
            }

            if (parsed is null)
            {
                var detail = FirstLine(run.StdErr) ?? FirstLine(run.StdOut);
                return ArchifyReviewResult.Fail(
                    $"visual-check 没有返回可解析的 JSON（退出码 {run.ExitCode}）。"
                    + (detail is null ? string.Empty : $" {detail}"));
            }

            var screenshots = parsed.Screenshots;
            var outputDirectory = Path.Combine(workspace, outputRelative.Replace('/', Path.DirectorySeparatorChar));
            var message = BuildMessage(parsed, run.ExitCode, screenshots.Count);

            return new ArchifyReviewResult(
                parsed.Ok && run.ExitCode == 0,
                message,
                parsed.Status,
                parsed.VisualReview,
                parsed.ContactSheet,
                parsed.Receipt,
                outputDirectory,
                screenshots);
        }
        catch (OperationCanceledException)
        {
            return ArchifyReviewResult.Fail("视觉复核已取消。");
        }
        catch (Exception ex)
        {
            WriteAudit(target, null, commandHash, ex.Message);
            return ArchifyReviewResult.Fail($"视觉复核失败：{ex.Message}");
        }
    }

    private static string? ResolveArtifact(string workspace, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Trim().Replace('\\', '/');

        if (normalized.StartsWith('/')
            || normalized.StartsWith('~')
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            return null;
        }

        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(workspace, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.TrimEndingDirectorySeparator(Path.Combine(workspace, ".archify"));

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               && File.Exists(full)
               && full.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private static string BuildMessage(ParsedReview parsed, int exitCode, int screenshotCount)
    {
        var checks = parsed.Checks.Count == 0
            ? "无 checks"
            : string.Join(", ", parsed.Checks.Select(item => $"{item.Key}={item.Value}"));
        var review = string.IsNullOrWhiteSpace(parsed.VisualReview)
            ? "pending"
            : parsed.VisualReview;

        var head = parsed.Ok && exitCode == 0
            ? $"自动浏览器校验 {parsed.Status ?? "pass"}（{checks}）"
            : $"视觉复核未通过（退出码 {exitCode}；status={parsed.Status ?? "unknown"}）";

        return $"{head}；visualReview={review}；截图 {screenshotCount} 张。"
               + (review.Equals("pending", StringComparison.OrdinalIgnoreCase)
                   ? "自动截图不等于人工视觉复核。"
                   : string.Empty);
    }

    private static ParsedReview? Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var line = stdout
            .Split('\n')
            .LastOrDefault(item => item.TrimStart().StartsWith('{'));

        if (line is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            var status = ReadString(root, "status");
            var visualReview = ReadString(root, "visualReview");
            var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("checks", out var checksElement)
                && checksElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in checksElement.EnumerateObject())
                {
                    checks[property.Name] = property.Value.ToString();
                }
            }

            var contactSheet = (string?)null;
            var receipt = (string?)null;
            var screenshots = new List<ArchifyScreenshot>();

            if (root.TryGetProperty("evidence", out var evidenceElement)
                && evidenceElement.ValueKind == JsonValueKind.Object)
            {
                contactSheet = ReadString(evidenceElement, "contactSheet");
                receipt = ReadString(evidenceElement, "receipt");

                if (evidenceElement.TryGetProperty("screenshots", out var shots)
                    && shots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var shot in shots.EnumerateArray())
                    {
                        var path = ReadString(shot, "path");

                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        screenshots.Add(new ArchifyScreenshot(
                            path,
                            ReadInt(shot, "width"),
                            ReadInt(shot, "height"),
                            ReadString(shot, "theme") ?? string.Empty,
                            ReadString(shot, "resolvedTheme") ?? string.Empty));
                    }
                }
            }

            return new ParsedReview(
                ok,
                status,
                visualReview,
                checks,
                contactSheet,
                receipt,
                screenshots);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return line is null ? null : line[..Math.Min(240, line.Length)];
    }

    private void WriteAudit(
        SkillScriptTarget target,
        SkillScriptRunResult? result,
        string commandHash,
        string? error)
    {
        _audit.Write(new ToolAuditEntry
        {
            SessionId = "artifact-preview",
            ToolId = "archify_visual_check",
            Sandbox = SandboxKind.None,
            ScriptSha256 = "sha256:" + target.Sha256,
            CommandSha256 = commandHash,
            ExitCode = result?.ExitCode,
            TimedOut = result?.TimedOut ?? false,
            DurationMs = result?.DurationMs ?? 0,
            StdoutBytes = result is null ? 0 : Encoding.UTF8.GetByteCount(result.StdOut),
            StderrBytes = result is null ? 0 : Encoding.UTF8.GetByteCount(result.StdErr),
            Approval = "NotRequired",
            Mode = "direct",
            WorkingDirectory = ".",
            Error = error
        });
    }

    private static string CommandHash(string scriptRelativePath, IReadOnlyList<string> arguments) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', new[] { scriptRelativePath }.Concat(arguments)))))
            .ToLowerInvariant();

    private sealed record ParsedReview(
        bool Ok,
        string? Status,
        string? VisualReview,
        IReadOnlyDictionary<string, string> Checks,
        string? ContactSheet,
        string? Receipt,
        IReadOnlyList<ArchifyScreenshot> Screenshots);
}
