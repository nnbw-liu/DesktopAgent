using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 执行沙箱设置。默认 Required + AppContainer：
/// 沙箱不可用时高风险工具直接拒绝执行，不允许回退到无沙箱直跑。
/// </summary>
public sealed class SandboxSettings
{
    /// <summary>沙箱总开关。关闭后 PowerShell / PDF 这类高风险工具必须整体不可用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Required 时沙箱不可用即拒绝执行；Preferred 允许降级到其他沙箱类型。</summary>
    public SandboxRequirement Requirement { get; set; } = SandboxRequirement.Required;

    /// <summary>首选沙箱类型（S1 起为 AppContainer，S4 可选 Windows Sandbox）。</summary>
    public SandboxKind Preferred { get; set; } = SandboxKind.AppContainer;

    /// <summary>
    /// Windows Sandbox 虚拟机内存（MB，下限由系统规定为 2048）。只影响 S4 的强隔离模式，
    /// 与 AppContainer 的 MemoryLimitMB 不是一回事。
    /// </summary>
    public int WindowsSandboxMemoryMB { get; set; } = 4096;

    /// <summary>单次执行超时秒数（S2 限制 1–120）。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>单进程内存上限（MB）。</summary>
    public int MemoryLimitMB { get; set; } = 512;

    /// <summary>整个 Job 的内存上限（MB）。</summary>
    public int JobMemoryLimitMB { get; set; } = 1024;

    /// <summary>Job 内允许的最大进程数。</summary>
    public int MaxProcesses { get; set; } = 8;

    /// <summary>工作区副本是否只读（S2 起生效）。</summary>
    public bool WorkspaceReadOnly { get; set; }

    /// <summary>工作区副本大小上限（MB），超过直接报错，不静默截断。</summary>
    public int WorkspaceCopyLimitMB { get; set; } = 200;

    /// <summary>审计日志是否保留脚本正文（默认只记 hash 与长度）。</summary>
    public bool RetainScriptsInAuditLog { get; set; }

    /// <summary>审计日志保留天数（S6 清理使用）。</summary>
    public int AuditRetentionDays { get; set; } = 7;
}
