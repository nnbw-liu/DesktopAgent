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

    /// <summary>审批结果：<c>ApproveOnce</c> / <c>Deny</c> / <c>NotRequired</c>。</summary>
    public string Approval { get; init; } = "NotRequired";

    public string? Error { get; init; }
}

/// <summary>工具审计写入入口。实现失败不能影响工具执行，所以实现内部吞掉 IO 异常。</summary>
public interface IToolAuditLog
{
    void Write(ToolAuditEntry entry);
}

/// <summary>
/// 每日一个 JSONL 文件：<c>{DataRoot}\logs\tools-yyyyMMdd.jsonl</c>。
/// S3 只负责写入与最小脱敏；保留天数、10MB 轮转与“打开日志目录”留给 S6。
/// </summary>
public sealed class JsonlToolAuditLog : IToolAuditLog
{
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
                    approval = payload.Approval,
                    error = payload.Error
                },
                Options);

            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                System.IO.File.AppendAllText(
                    FilePathFor(now.LocalDateTime),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 审计失败不影响工具执行。
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
