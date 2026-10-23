namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// 沙箱自检报告。每个 Positive 项为 true 表示“符合预期”；
/// <see cref="CannotReadUserProfile"/> / <see cref="CannotWriteWorkspace"/> / <see cref="NoNetwork"/>
/// 为 true 表示隔离生效（即被拒绝），不是“能力可用”。
/// </summary>
public sealed record SandboxProbeReport(
    bool CmdOk,
    bool PowerShellOk,
    bool CannotReadUserProfile,
    bool CannotWriteWorkspace,
    bool NoNetwork,
    bool SubprocessOk,
    bool TimeoutOk,
    string? Detail)
{
    /// <summary>所有检查项都符合预期。</summary>
    public bool AllPassed =>
        CmdOk && PowerShellOk && CannotReadUserProfile && CannotWriteWorkspace
        && NoNetwork && SubprocessOk && TimeoutOk;
}
