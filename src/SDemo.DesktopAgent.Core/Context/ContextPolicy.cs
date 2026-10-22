using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 把「模型档案 + 用户设置」翻译成裁剪参数。所有策略决策集中在这里，
/// 裁剪器只执行，不在内部散落 <c>if (window == 8192)</c> 之类的判断。
/// </summary>
public sealed class ContextPolicy
{
    public ContextPolicy(ModelProfile profile, ContextSettings settings)
    {
        Profile = profile;
        Settings = settings;
        Budget = ContextBudget.Create(profile, settings);
    }

    public ModelProfile Profile { get; }

    public ContextSettings Settings { get; }

    public ContextBudget Budget { get; }

    public bool Enabled => Settings.Enabled;

    /// <summary>小模型（≤ 8K）保留更少的完整轮次。</summary>
    public int KeepRecentTurns => Profile.IsSmallModel
        ? Math.Clamp(Settings.SmallModelKeepRecentTurns, 1, 50)
        : Math.Clamp(Settings.KeepRecentTurns, 1, 50);

    public int ToolTraceTokenBudget => Math.Max(0, Settings.ToolTraceTokenBudget);

    public int MaxPinnedMessages => Math.Max(0, Settings.MaxPinnedMessages);

    public int PinnedTruncateChars => Math.Max(0, Settings.PinnedTruncateChars);

    public double PinnedShareLimit => Math.Clamp(Settings.PinnedShareLimit, 0.0, 1.0);

    public int MidTurnToolResultChars => Math.Max(0, Settings.MidTurnToolResultChars);

    /// <summary>
    /// 当前轮附件内联上限（字符）。按 token 预算换算成字符时取 2 字符/token：
    /// 对中文略保守，对英文足够；随后裁剪器还会按实际估算收紧。
    /// </summary>
    public int AttachmentInlineChars =>
        Math.Max(256, Math.Min(48 * 1024, Math.Max(0, Settings.AttachmentInlineTokens) * 2));

    /// <summary>P2 用：压缩目标；P1 只用于报告展示。</summary>
    public int CompactTarget => Budget.CompactTarget;
}
