using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 当前策略的宿主事实（P2a `policy` 段的数据源）。
/// <para>
/// 提示词里只有「怎么做」的规则，没有「现在能做什么」：用户切到直跑、把工作区副本改成只读、
/// 或者关掉审批之后，模型完全不知道。这里把那些事实抽成结构化对象，让 `policy` 段始终在场——
/// 这条不只是体验，是安全属性（DSH 把 sandbox / approval 做成独立 required 段也是这个理由）。
/// </para>
/// </summary>
public sealed record AgentPolicyFacts(
    bool SandboxEnabled,
    SandboxKind SandboxKind,
    SandboxRequirement SandboxRequirement,
    bool WorkspaceReadOnly,
    ExecutionMode ExecutionMode,
    bool DirectRunEnabled,
    IReadOnlyList<string> CommandAllowlist,
    bool ConfirmFileWrite)
{
    /// <summary>默认策略：沙箱必需（AppContainer）、不直跑、写文件要确认。</summary>
    public static AgentPolicyFacts Default { get; } = new(
        SandboxEnabled: true,
        SandboxKind.AppContainer,
        SandboxRequirement.Required,
        WorkspaceReadOnly: false,
        ExecutionMode.SandboxFirst,
        DirectRunEnabled: false,
        CommandAllowlist: [],
        ConfirmFileWrite: true);

    /// <summary>
    /// 从设置里取事实：执行设置与「写文件确认」走有效设置（含工作区 / 聊天室覆盖），
    /// 沙箱配置只有全局一份。
    /// </summary>
    public static AgentPolicyFacts FromSettings(AppSettings global, EffectiveSettings? scope)
    {
        var sandbox = global.Sandbox ?? new SandboxSettings();
        var execution = scope?.Execution ?? global.Execution ?? new ExecutionSettings();
        var allowlist = execution.Allowlist?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToArray() ?? [];

        return new AgentPolicyFacts(
            sandbox.Enabled,
            sandbox.Preferred,
            sandbox.Requirement,
            sandbox.WorkspaceReadOnly,
            execution.Mode,
            execution.DirectRunEnabled,
            allowlist,
            scope?.ConfirmFileWrite ?? global.ConfirmFileWrite);
    }

    /// <summary>本轮允许在宿主直跑（白名单 + 开关同时满足）。</summary>
    public bool DirectRunAllowed =>
        DirectRunEnabled && ExecutionMode == ExecutionMode.AllowlistDirect;
}
