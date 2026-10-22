using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一条工具审计记录。默认只记脚本 hash 与长度，不记脚本正文、不记 stdout / stderr 内容，
/// 避免日志泄密（§3.5 #11）。
/// </summary>
public sealed record ToolAuditEntry
{
    public string SessionId { get; init; } = string.Empty;

    public string ToolId { get; init; } = string.Empty;

    public string JobId { get; init; } = string.Empty;

    public SandboxKind Sandbox { get; init; }

    /// <summary>脚本 SHA-256，格式 <c>sha256:...</c>；没有脚本的工具为 null。</summary>
    public string? ScriptSha256 { get; init; }

    /// <summary>仅在设置里打开 <c>RetainScriptsInAuditLog</c> 时写入（前 4KB）。</summary>
    public string? ScriptPreview { get; init; }

    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public long DurationMs { get; init; }

    public int StdoutBytes { get; init; }

    public int StderrBytes { get; init; }

    public int ChangeCount { get; init; }

    /// <summary>变更明细：只记类型与路径 hash，不记路径原文或文件内容。</summary>
    public IReadOnlyList<ToolAuditChange>? Changes { get; init; }

    /// <summary>审批结果：<c>ApproveOnce</c> / <c>Deny</c> / <c>NotRequired</c>。</summary>
    public string Approval { get; init; } = "NotRequired";

    public string? Error { get; init; }
}

/// <summary>一条变更的审计摘要：<c>Added</c> / <c>Modified</c> / <c>Deleted</c> + 路径 hash。</summary>
public sealed record ToolAuditChange(string Kind, string PathSha256);

/// <summary>工具审计写入入口。实现失败不能影响工具执行，所以实现内部吞掉 IO 异常。</summary>
public interface IToolAuditLog
{
    void Write(ToolAuditEntry entry);
}

/// <summary>
/// 每日一个 JSONL 文件：<c>{DataRoot}\logs\tools-yyyyMMdd.jsonl</c>。
/// S6 起：按 <c>AuditRetentionDays</c> 清理过期文件，单文件超过 10MB 轮转为
/// <c>tools-yyyyMMdd-{n}.jsonl</c>；仍不写脚本正文与 stdout / stderr 内容。
/// </summary>
public sealed class JsonlToolAuditLog : IToolAuditLog
{
    public const long MaxFileBytes = 10L * 1024 * 1024;

    private const int MaxPreviewChars = 4 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private bool _housekeepingDone;

    public JsonlToolAuditLog(IAppPaths paths, ISettingsService settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public string DirectoryPath => Path.Combine(_paths.DataRoot, "logs");

    public string FilePathFor(DateTime localTime) =>
        Path.Combine(DirectoryPath, $"tools-{localTime:yyyyMMdd}.jsonl");

    public void Write(ToolAuditEntry entry)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var payload = entry with
            {
                // 默认脱敏：即使调用方传了 preview，没有设置开关也不写。
                ScriptPreview = _settings.Current.Sandbox.RetainScriptsInAuditLog
                    ? Truncate(entry.ScriptPreview, MaxPreviewChars)
                    : null
            };

            var line = JsonSerializer.Serialize(
                new
                {
                    ts = now.ToString("O"),
                    sessionId = payload.SessionId,
                    toolId = payload.ToolId,
                    jobId = payload.JobId,
                    sandbox = payload.Sandbox.ToString(),
                    scriptSha256 = payload.ScriptSha256,
                    scriptPreview = payload.ScriptPreview,
                    exitCode = payload.ExitCode,
                    timedOut = payload.TimedOut,
                    durationMs = payload.DurationMs,
                    stdoutBytes = payload.StdoutBytes,
                    stderrBytes = payload.StderrBytes,
                    changeCount = payload.ChangeCount,
                    changes = payload.Changes,
                    approval = payload.Approval,
                    error = payload.Error
                },
                Options);

            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                EnsureHousekeeping(now.LocalDateTime);
                var path = RotateIfNeeded(FilePathFor(now.LocalDateTime), now.LocalDateTime);
                System.IO.File.AppendAllText(
                    path,
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 审计失败不影响工具执行。
        }
    }

    /// <summary>每个进程只做一次：删除超过保留天数的 <c>tools-*.jsonl</c>。失败不影响写日志。</summary>
    private void EnsureHousekeeping(DateTime now)
    {
        if (_housekeepingDone)
        {
            return;
        }

        _housekeepingDone = true;

        try
        {
            var retentionDays = Math.Clamp(_settings.Current.Sandbox.AuditRetentionDays, 1, 365);
            var cutoff = now.ToUniversalTime().AddDays(-retentionDays);

            foreach (var file in System.IO.Directory.EnumerateFiles(DirectoryPath, "tools-*.jsonl"))
            {
                try
                {
                    if (System.IO.File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        System.IO.File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 被占用的旧日志留到下次启动再清。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 维护失败不影响本次写入。
        }
    }

    /// <summary>当前文件达到 10MB 时改名为带序号的历史文件，再继续写当天主文件。</summary>
    private static string RotateIfNeeded(string path, DateTime now)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return path;
            }

            for (var index = 1; ; index++)
            {
                var candidate = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    $"tools-{now:yyyyMMdd}-{index}.jsonl");

                if (!System.IO.File.Exists(candidate))
                {
                    System.IO.File.Move(path, candidate);
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 轮转失败就继续追加：宁可单文件超一点，也不要丢审计记录。
            return path;
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
