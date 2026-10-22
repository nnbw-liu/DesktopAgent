namespace SDemo.DesktopAgent.Sandbox.Protocol;

/// <summary>
/// 宿主与 <c>SDemo.DesktopAgent.SandboxHost</c> 之间的文件协议常量。
/// 版本不匹配时两端都必须拒绝，不要尝试兼容旧字段。
/// </summary>
public static class SandboxProtocol
{
    public const int Version = 1;
    public const string JobFileName = "job.json";
    public const string ResultFileName = "result.json";
    public const string PdfResultFileName = "pdf.json";
    public const string ManifestFileName = "manifest.json";

    /// <summary>PDF 作业固定复制到 <c>input/document.pdf</c>：不接受用户路径，避免路径注入。</summary>
    public const string PdfInputFileName = "document.pdf";
}

/// <summary>job.json 里声明的作业模式。序列化为字符串，禁止写出数字。</summary>
public enum SandboxJobMode
{
    PowerShell,
    PdfExtract,
    PdfInfo
}

/// <summary>宿主写入 <c>input/job.json</c> 的作业描述。</summary>
public sealed record SandboxJobDocument
{
    public int ProtocolVersion { get; init; } = SandboxProtocol.Version;
    public required string JobId { get; init; }
    public required SandboxJobMode Mode { get; init; }
    public string? ScriptFile { get; init; }
    public string? InputFile { get; init; }
    public string? Pages { get; init; }
    public int MaxChars { get; init; } = 20_000;
    public string WorkingDirectory { get; init; } = "work";
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxOutputBytes { get; init; } = 8 * 1024 * 1024;
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>SandboxHost 原子写入 <c>output/result.json</c> 的执行结果。</summary>
public sealed record SandboxResultDocument
{
    public int ProtocolVersion { get; init; } = SandboxProtocol.Version;
    public required string JobId { get; init; }
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Canceled { get; init; }
    public bool Truncated { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    public string? Error { get; init; }
}
