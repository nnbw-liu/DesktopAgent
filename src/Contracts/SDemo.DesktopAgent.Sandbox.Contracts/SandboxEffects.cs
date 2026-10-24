namespace SDemo.DesktopAgent.Sandbox;

/// <summary>一次沙箱执行可能产生了哪些类别的副作用。</summary>
public enum SandboxEffect
{
    /// <summary>只改动了工作区副本里的文件（可以安全地应用回真实工作区）。</summary>
    FileChanges,

    /// <summary>脚本里出现了联网调用（沙箱本身无网络，但脚本意图值得提示）。</summary>
    Network,

    /// <summary>关机 / 注册表 / 服务 / 提权这类系统级操作：不允许在工作区重放。</summary>
    SystemLevel
}

/// <summary>
/// 沙箱结果的副作用判定。注意：这只是**提示**，不是安全证明——
/// 沙箱证明不了脚本安全，真正的安全来自「人看过脚本后批准」。
/// </summary>
public sealed record SandboxEffectReport(
    IReadOnlyList<SandboxEffect> Effects,
    IReadOnlyList<string> Signals)
{
    public static readonly SandboxEffectReport None = new([], []);

    public bool Has(SandboxEffect effect) => Effects.Contains(effect);

    public bool HasSystemLevel => Has(SandboxEffect.SystemLevel);

    public bool HasNetwork => Has(SandboxEffect.Network);

    public bool HasFileChanges => Has(SandboxEffect.FileChanges);

    /// <summary>能否把这个脚本原样拿到真实工作区重跑：系统级操作一律不允许。</summary>
    public bool CanReplayInWorkspace => !HasSystemLevel;
}
