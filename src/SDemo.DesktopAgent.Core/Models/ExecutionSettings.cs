namespace SDemo.DesktopAgent.Models;

/// <summary>命令执行模式。</summary>
public enum ExecutionMode
{
    /// <summary>所有命令都进沙箱（默认）。</summary>
    SandboxFirst,

    /// <summary>白名单命令可以在宿主直跑（仍需审批，可申请会话授权）。</summary>
    AllowlistDirect
}

/// <summary>
/// 直跑设置。默认关闭；打开后只放行白名单里的可执行文件，且仍经过参数校验与审批。
/// </summary>
public sealed class ExecutionSettings
{
    public ExecutionMode Mode { get; set; } = ExecutionMode.SandboxFirst;

    /// <summary>显式开关；与 <see cref="Mode"/> 同时满足才允许直跑。</summary>
    public bool DirectRunEnabled { get; set; }

    public List<string> Allowlist { get; set; } =
        ["dotnet", "npm", "pnpm", "node", "git", "python", "cargo", "go"];

    public int DefaultTimeoutSeconds { get; set; } = 120;

    public int BuildTimeoutSeconds { get; set; } = 600;

    public int MaxOutputChars { get; set; } = 12_000;
}

/// <summary>热工作区（P4 可选，默认关闭）。</summary>
public sealed class WarmWorkspaceSettings
{
    public bool Enabled { get; set; }

    public int IdleMinutes { get; set; } = 30;
}
