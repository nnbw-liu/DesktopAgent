namespace SDemo.DesktopAgent.Sandbox;

/// <summary>沙箱类型。None 表示没有可用沙箱（宿主必须按策略拒绝执行，不能静默降级）。</summary>
public enum SandboxKind
{
    None,
    JobObject,
    AppContainer,
    WindowsSandbox,
    Remote
}

/// <summary>工具对沙箱的要求。Required 时沙箱不可用必须拒绝执行。</summary>
public enum SandboxRequirement
{
    None,
    Preferred,
    Required
}

/// <summary>沙箱失败原因。工具结果与审计日志都用它，避免只报“失败了”。</summary>
public enum SandboxFailureCode
{
    None,
    NotAvailable,
    ProbeFailed,
    LaunchFailed,
    Timeout,
    Canceled,
    OutputLimit,
    ProtocolError,
    HostCrashed,
    WorkspaceTooLarge,
    PathEscape
}

/// <summary>单次沙箱执行的硬限制（超时 / 内存 / 进程数 / 输出上限）。</summary>
public sealed record SandboxLimits
{
    public int TimeoutSeconds { get; init; } = 30;
    public int MemoryLimitMB { get; init; } = 512;
    public int JobMemoryLimitMB { get; init; } = 1024;
    public int MaxProcesses { get; init; } = 8;
    public int MaxOutputBytes { get; init; } = 8 * 1024 * 1024;
}

/// <summary>宿主交给 <c>ISandboxRunner</c> 的一次执行请求。</summary>
public sealed record SandboxRequest
{
    public required string JobId { get; init; }
    public required SandboxKind Kind { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public required string OutputDirectory { get; init; }
    public SandboxLimits Limits { get; init; } = new();
}

/// <summary>一次沙箱执行的结果。进程没有真正跑起来时用 <see cref="Failure"/> 说明原因。</summary>
public sealed record SandboxResult
{
    public required string JobId { get; init; }
    public required SandboxKind Kind { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Canceled { get; init; }
    public SandboxFailureCode Failure { get; init; }
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// 沙箱可用性。<see cref="Available"/> 只表示沙箱能启动进程；
/// PowerShell 必须要求 <see cref="Available"/> 与 <see cref="Verified"/> 同时为真。
/// </summary>
public sealed record SandboxAvailability(
    SandboxKind Kind,
    bool Available,
    bool Verified,
    string? Reason,
    string? Detail);

/// <summary>执行策略：是否必须沙箱、首选哪种沙箱、工作区是否只读。</summary>
public sealed record SandboxPolicy(
    SandboxRequirement Requirement = SandboxRequirement.Required,
    SandboxKind Preferred = SandboxKind.AppContainer,
    bool WorkspaceReadOnly = false);

/// <summary>沙箱内工作区副本相对快照的变更类型。</summary>
public enum SandboxChangeKind
{
    Added,
    Modified,
    Deleted
}

/// <summary>一条待用户确认的变更。S2 计算，S3 展示并应用。</summary>
public sealed record SandboxChange(
    string RelativePath,
    SandboxChangeKind Kind,
    long Size,
    bool IsBinary,
    string? Diff);

/// <summary>一次沙箱作业的完整结果：执行结果 + 变更集 + job 目录（S2 之后填充）。</summary>
public sealed record SandboxJobOutcome(
    string SessionId,
    string JobId,
    SandboxResult Result,
    IReadOnlyList<SandboxChange> Changes,
    string JobDirectory);
