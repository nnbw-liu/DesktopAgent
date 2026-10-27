using SDemo.DesktopAgent.Execution;

namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 通用 → 工作区 → 聊天室 合并后的有效设置。
/// 运行时只读这份快照，避免同一台机器上不同聊天室互相覆盖。
/// </summary>
public sealed class EffectiveSettings
{
    public required ContextSettings Context { get; init; }

    public required AgentSettings Agent { get; init; }

    public required ModelRoutingSettings ModelRouting { get; init; }

    public required ExecutionSettings Execution { get; init; }

    public required CheckpointSettings Checkpoints { get; init; }

    /// <summary>解析后的执行沙箱设置；聊天室的开关已合并进 <see cref="SandboxSettings.Enabled"/>。</summary>
    public required SandboxSettings Sandbox { get; init; }

    public required bool ConfirmFileWrite { get; init; }

    /// <summary>
    /// 本作用域是否跳过工具审批（「完全权限」）。工具包不直接读它——
    /// <c>ScopedApprovalGate</c> 在构造 <c>ToolContext</c> 时按它替换审批服务，
    /// 这样所有工具包的确认框走同一个开关。
    /// </summary>
    public bool SkipApprovals { get; init; }

    /// <summary>解析后的模型提供商；调用方按 id 到通用设置里找 provider 对象。</summary>
    public string? ProviderId { get; init; }

    /// <summary>解析后的模型 id。</summary>
    public string? ModelId { get; init; }

    /// <summary>作用域是否显式打开 / 关闭自动路由；null 时跟随全局 <see cref="ModelRoutingSettings.Mode"/>。</summary>
    public bool? AutoModelRouting { get; init; }

    /// <summary>
    /// 本轮是否启用自动选模型。全局模式是总开关（Off 时任何作用域都不路由）；
    /// 非 Off 时，作用域显式 false 表示固定模型，null / true 表示跟随自动。
    /// </summary>
    public bool UseModelRouting =>
        ModelRouting.Mode != ModelRoutingMode.Off && (AutoModelRouting ?? true);

    public static EffectiveSettings Resolve(
        AppSettings global,
        Workspace? workspace,
        ChatSession? session)
    {
        var resolved = new Resolved(global);

        resolved.Apply(workspace?.ScopedSettings);
        resolved.Apply(session?.ScopedSettings);

        var providerId = global.LastProviderId;
        var modelId = global.LastModelId;

        // 旧会话如果已经显式选过模型（没有 AutoModelRouting 标记），视为固定模型，
        // 不因为后来打开全局路由而悄悄换模型；用户可以在模型选择器里重新选「自动」。
        if (resolved.AutoModelRouting is null
            && (HasModelPair(session?.ScopedSettings) || HasModelPair(workspace?.ScopedSettings)))
        {
            resolved.AutoModelRouting = false;
        }

        // provider / model 必须成对；只设了一个就整对忽略，避免拿 A 家的模型去请求 B 家。
        if (HasModelPair(workspace?.ScopedSettings))
        {
            providerId = workspace!.ScopedSettings.ProviderId;
            modelId = workspace.ScopedSettings.ModelId;
        }

        if (HasModelPair(session?.ScopedSettings))
        {
            providerId = session!.ScopedSettings.ProviderId;
            modelId = session.ScopedSettings.ModelId;
        }

        return new EffectiveSettings
        {
            Context = resolved.Context,
            Agent = resolved.Agent,
            ModelRouting = resolved.ModelRouting,
            Execution = resolved.Execution,
            Checkpoints = resolved.Checkpoints,
            Sandbox = resolved.Sandbox,
            ConfirmFileWrite = resolved.ConfirmFileWrite,
            SkipApprovals = resolved.SkipApprovals,
            ProviderId = providerId,
            ModelId = modelId,
            AutoModelRouting = resolved.AutoModelRouting
        };
    }

    private static bool HasModelPair(ScopedSettings? scope) =>
        !string.IsNullOrWhiteSpace(scope?.ProviderId)
        && !string.IsNullOrWhiteSpace(scope.ModelId);

    /// <summary>
    /// 逐层合并的可写状态。每一层只覆盖自己显式设过的项；
    /// 通用设置先克隆一份，避免作用域解析改到全局对象。
    /// </summary>
    private sealed class Resolved
    {
        public Resolved(AppSettings global)
        {
            Context = (global.Context ?? new ContextSettings()).Clone();
            Agent = (global.Agent ?? new AgentSettings()).Clone();
            ModelRouting = (global.ModelRouting ?? new ModelRoutingSettings()).Clone();
            Execution = (global.Execution ?? new ExecutionSettings()).Clone();
            Checkpoints = (global.Checkpoints ?? new CheckpointSettings()).Clone();
            Sandbox = (global.Sandbox ?? new SandboxSettings()).Clone();
            ConfirmFileWrite = global.ConfirmFileWrite;
        }

        public ContextSettings Context { get; }

        public AgentSettings Agent { get; }

        public ModelRoutingSettings ModelRouting { get; }

        public ExecutionSettings Execution { get; }

        public CheckpointSettings Checkpoints { get; }

        public SandboxSettings Sandbox { get; }

        public bool ConfirmFileWrite { get; set; }

        public bool SkipApprovals { get; set; }

        public bool? AutoModelRouting { get; set; }

        public void Apply(ScopedSettings? scope)
        {
            if (scope is null)
            {
                return;
            }

            if (scope.AutoModelRouting is bool autoRouting)
            {
                AutoModelRouting = autoRouting;
            }

            if (scope.ContextEnabled is bool contextEnabled)
            {
                Context.Enabled = contextEnabled;
            }

            if (scope.BudgetRatio is double budgetRatio)
            {
                Context.BudgetRatio = budgetRatio;
            }

            if (scope.KeepRecentTurns is int keepRecentTurns)
            {
                Context.KeepRecentTurns = keepRecentTurns;
            }

            if (scope.AutoCompact is bool autoCompact)
            {
                Context.AutoCompact = autoCompact;
            }

            if (scope.StatePackEnabled is bool statePackEnabled)
            {
                Context.StatePackEnabled = statePackEnabled;
            }

            if (scope.ShowContextMeter is bool showContextMeter)
            {
                Context.ShowContextMeter = showContextMeter;
            }

            if (scope.MaxPinnedMessages is int maxPinnedMessages)
            {
                Context.MaxPinnedMessages = maxPinnedMessages;
            }

            if (scope.AgentMode is Context.AgentModePreference mode)
            {
                Agent.Mode = mode;
            }

            if (scope.SearchCapabilitiesEnabled is bool searchCapabilities)
            {
                Agent.SearchCapabilitiesEnabled = searchCapabilities;
            }

            if (scope.SkillIndexEnabled is bool skillIndex)
            {
                Agent.SkillIndexEnabled = skillIndex;
            }

            if (scope.WorkspaceMemoryEnabled is bool workspaceMemory)
            {
                Agent.WorkspaceMemoryEnabled = workspaceMemory;
            }

            if (scope.ExposeAllTools is bool exposeAllTools)
            {
                Agent.ExposeAllTools = exposeAllTools;
            }

            if (scope.WriteFileShrinkGuard is bool shrinkGuard)
            {
                Agent.WriteFileShrinkGuard = shrinkGuard;
            }

            if (scope.ConfirmFileWrite is bool confirmWrite)
            {
                ConfirmFileWrite = confirmWrite;
            }

            if (scope.CheckpointsEnabled is bool checkpointsEnabled)
            {
                Checkpoints.Enabled = checkpointsEnabled;
            }

            if (scope.DirectRunEnabled is bool directRun)
            {
                Execution.DirectRunEnabled = directRun;
                Execution.Mode = directRun ? ExecutionMode.AllowlistDirect : ExecutionMode.SandboxFirst;
            }

            if (scope.SandboxEnabled is bool sandboxEnabled)
            {
                Sandbox.Enabled = sandboxEnabled;
            }

            if (scope.SkipApprovals is bool skipApprovals)
            {
                SkipApprovals = skipApprovals;
            }
        }
    }
}
