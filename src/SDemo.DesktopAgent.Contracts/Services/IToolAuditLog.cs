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

    /// <summary>审批结果：<c>ApproveOnce</c> / <c>SessionGrant</c> / <c>Deny</c> / <c>NotRequired</c>。</summary>
    public string Approval { get; init; } = "NotRequired";

    /// <summary>执行模式（P4）：<c>sandbox</c> / <c>direct</c>；非执行类工具为 null。</summary>
    public string? Mode { get; init; }

    /// <summary>整条命令（含参数）的 SHA-256，避免审计日志记录命令正文。</summary>
    public string? CommandSha256 { get; init; }

    /// <summary>直跑工作目录（相对工作区）。</summary>
    public string? WorkingDirectory { get; init; }

    public string? Error { get; init; }
}

/// <summary>一条变更的审计摘要：<c>Added</c> / <c>Modified</c> / <c>Deleted</c> + 路径 hash。</summary>
public sealed record ToolAuditChange(string Kind, string PathSha256);

/// <summary>工具审计写入入口。实现失败不能影响工具执行，所以实现内部吞掉 IO 异常。</summary>
public interface IToolAuditLog
{
    void Write(ToolAuditEntry entry);
}
