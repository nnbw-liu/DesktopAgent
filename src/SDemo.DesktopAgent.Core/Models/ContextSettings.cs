namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 上下文预算与裁剪设置（settings.json 的 <c>context</c> 节点）。
/// 旧 settings.json 没有这个节点时按默认值处理，不做迁移。
/// </summary>
public sealed class ContextSettings
{
    /// <summary>总开关；关闭后退回「全量历史」旧行为（用于对照排查）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>输入预算占可用窗口的比例（§3.4，默认 0.85）。</summary>
    public double BudgetRatio { get; set; } = 0.85;

    /// <summary>最近多少轮完整保留（普通模型）。</summary>
    public int KeepRecentTurns { get; set; } = 6;

    /// <summary>8K 及以下模型的保留轮数。</summary>
    public int SmallModelKeepRecentTurns { get; set; } = 4;

    /// <summary>系统提示词 + 技能等固定块占总预算的上限比例。</summary>
    public double FixedCapRatio { get; set; } = 0.40;

    /// <summary>超预算时自动压缩。P1 只落配置，P2 生效。</summary>
    public bool AutoCompact { get; set; } = true;

    /// <summary>工具循环内部的请求也做 L1 裁剪（§3.7 分支 A）。</summary>
    public bool MidTurnTrim { get; set; } = true;

    /// <summary>400 溢出时学习窗口并按半预算重试一次。</summary>
    public bool OverflowRetry { get; set; } = true;

    /// <summary>是否在输入框显示上下文占用环。</summary>
    public bool ShowContextMeter { get; set; } = true;

    /// <summary>回放工具轨迹的总 token 预算（§3.5 规则 6）。</summary>
    public int ToolTraceTokenBudget { get; set; } = 300;

    /// <summary>当前轮附件最多内联多少 token；超出部分改成「已省略 + 路径」桩。</summary>
    public int AttachmentInlineTokens { get; set; } = 4096;

    /// <summary>工具循环内单条工具结果的字符上限（§3.7 兜底，A/B 分支都生效）。</summary>
    public int MidTurnToolResultChars { get; set; } = 2000;

    /// <summary>pin 消息数量上限。</summary>
    public int MaxPinnedMessages { get; set; } = 20;

    /// <summary>pin 内容超过总预算的这个比例时截断（§3.5 规则 8）。</summary>
    public double PinnedShareLimit { get; set; } = 0.30;

    /// <summary>pin 被截断后保留的字符数。</summary>
    public int PinnedTruncateChars { get; set; } = 500;

    /// <summary>溢出重试时的预算缩放比例（§3.8：按 total × 0.5 重算）。</summary>
    public double OverflowRetryScale { get; set; } = 0.5;
}
