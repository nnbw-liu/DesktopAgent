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

    public required bool ConfirmFileWrite { get; init; }

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
        var context = (global.Context ?? new ContextSettings()).Clone();
        var agent = (global.Agent ?? new AgentSettings()).Clone();
        var modelRouting = (global.ModelRouting ?? new ModelRoutingSettings()).Clone();
        var execution = (global.Execution ?? new ExecutionSettings()).Clone();
        var checkpoints = (global.Checkpoints ?? new CheckpointSettings()).Clone();
        var confirmFileWrite = global.ConfirmFileWrite;
        bool? autoModelRouting = null;

        Apply(workspace?.ScopedSettings, context, agent, execution, checkpoints, ref confirmFileWrite, ref autoModelRouting);
        Apply(session?.ScopedSettings, context, agent, execution, checkpoints, ref confirmFileWrite, ref autoModelRouting);

        var providerId = global.LastProviderId;
        var modelId = global.LastModelId;

        // 旧会话如果已经显式选过模型（没有 AutoModelRouting 标记），视为固定模型，
        // 不因为后来打开全局路由而悄悄换模型；用户可以在模型选择器里重新选「自动」。
        if (autoModelRouting is null
            && (HasModelPair(session?.ScopedSettings) || HasModelPair(workspace?.ScopedSettings)))
        {
            autoModelRouting = false;
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
            Context = context,
            Agent = agent,
            ModelRouting = modelRouting,
            Execution = execution,
            Checkpoints = checkpoints,
            ConfirmFileWrite = confirmFileWrite,
            ProviderId = providerId,
            ModelId = modelId,
            AutoModelRouting = autoModelRouting
        };
    }

    private static bool HasModelPair(ScopedSettings? scope) =>
        !string.IsNullOrWhiteSpace(scope?.ProviderId)
        && !string.IsNullOrWhiteSpace(scope.ModelId);

    private static void Apply(
        ScopedSettings? scope,
        ContextSettings context,
        AgentSettings agent,
        ExecutionSettings execution,
        CheckpointSettings checkpoints,
        ref bool confirmFileWrite,
        ref bool? autoModelRouting)
    {
        if (scope is null)
        {
            return;
        }

        if (scope.AutoModelRouting is bool autoRouting)
        {
            autoModelRouting = autoRouting;
        }

        if (scope.ContextEnabled is bool contextEnabled)
        {
            context.Enabled = contextEnabled;
        }

        if (scope.BudgetRatio is double budgetRatio)
        {
            context.BudgetRatio = budgetRatio;
        }

        if (scope.KeepRecentTurns is int keepRecentTurns)
        {
            context.KeepRecentTurns = keepRecentTurns;
        }

        if (scope.AutoCompact is bool autoCompact)
        {
            context.AutoCompact = autoCompact;
        }

        if (scope.StatePackEnabled is bool statePackEnabled)
        {
            context.StatePackEnabled = statePackEnabled;
        }

        if (scope.ShowContextMeter is bool showContextMeter)
        {
            context.ShowContextMeter = showContextMeter;
        }

        if (scope.MaxPinnedMessages is int maxPinnedMessages)
        {
            context.MaxPinnedMessages = maxPinnedMessages;
        }

        if (scope.AgentMode is Context.AgentModePreference mode)
        {
            agent.Mode = mode;
        }

        if (scope.SearchCapabilitiesEnabled is bool searchCapabilities)
        {
            agent.SearchCapabilitiesEnabled = searchCapabilities;
        }

        if (scope.SkillIndexEnabled is bool skillIndex)
        {
            agent.SkillIndexEnabled = skillIndex;
        }

        if (scope.WorkspaceMemoryEnabled is bool workspaceMemory)
        {
            agent.WorkspaceMemoryEnabled = workspaceMemory;
        }

        if (scope.ExposeAllTools is bool exposeAllTools)
        {
            agent.ExposeAllTools = exposeAllTools;
        }

        if (scope.WriteFileShrinkGuard is bool shrinkGuard)
        {
            agent.WriteFileShrinkGuard = shrinkGuard;
        }

        if (scope.ConfirmFileWrite is bool confirmWrite)
        {
            confirmFileWrite = confirmWrite;
        }

        if (scope.CheckpointsEnabled is bool checkpointsEnabled)
        {
            checkpoints.Enabled = checkpointsEnabled;
        }

        if (scope.DirectRunEnabled is bool directRun)
        {
            execution.DirectRunEnabled = directRun;
            execution.Mode = directRun ? ExecutionMode.AllowlistDirect : ExecutionMode.SandboxFirst;
        }
    }
}
