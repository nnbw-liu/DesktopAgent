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

    /// <summary>超预算时自动压缩（增量摘要 + 状态包）。P2 生效。</summary>
    public bool AutoCompact { get; set; } = true;

    /// <summary>摘要模型的 provider id；为空表示用当前会话的 provider。</summary>
    public string? SummaryProviderId { get; set; }

    /// <summary>摘要模型的 id；为空表示用当前会话的模型。</summary>
    public string? SummaryModelId { get; set; }

    /// <summary>摘要正文的 token 上限（默认 800；同时用于压缩目标校验）。</summary>
    public int SummaryMaxTokens { get; set; } = 800;

    /// <summary>单次喂给摘要模型的历史 chunk 上限（默认 6000 token，超过则分块增量摘要）。</summary>
    public int SummaryInputMaxTokens { get; set; } = 6000;

    /// <summary>摘要请求超时（秒）；超时走确定性兜底。</summary>
    public int SummaryTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// 是否把工作区状态（结构 / 最近改动 / 最近动作）放进每轮末尾的宿主上下文快照。
    /// 关闭后模型只能靠历史与工具重新读取文件；策略、验证、时间三段不受这个开关影响。
    /// </summary>
    public bool StatePackEnabled { get; set; } = true;

    /// <summary>状态包 token 上限（普通模型）。</summary>
    public int StatePackTokenBudget { get; set; } = 800;

    /// <summary>状态包 token 上限（≤8K 模型，避免挤掉历史）。</summary>
    public int StatePackSmallModelTokenBudget { get; set; } = 500;

    /// <summary>是否注入 <c>time</c> 段（宿主时钟 + 时区 + 距上一条模型可见消息的时长）。</summary>
    public bool TimeContextEnabled { get; set; } = true;

    /// <summary>是否注入 <c>plan</c> 段（会话当前计划）；没有计划时该段本来就零成本。</summary>
    public bool PlanContextEnabled { get; set; } = true;

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

    /// <summary>
    /// 超过 <see cref="MidTurnToolResultChars"/> 的工具结果把全文落盘到 <c>&lt;workspace&gt;/.agent/spill</c>，
    /// 上下文里只留头尾预览与 <c>read_file</c> 回读指引（P2c）。关闭后退回纯截断。
    /// </summary>
    public bool SpillLargeToolOutputs { get; set; } = true;

    /// <summary>pin 消息数量上限。</summary>
    public int MaxPinnedMessages { get; set; } = 20;

    /// <summary>pin 内容超过总预算的这个比例时截断（§3.5 规则 8）。</summary>
    public double PinnedShareLimit { get; set; } = 0.30;

    /// <summary>pin 被截断后保留的字符数。</summary>
    public int PinnedTruncateChars { get; set; } = 500;

    /// <summary>溢出重试时的预算缩放比例（§3.8：按 total × 0.5 重算）。</summary>
    public double OverflowRetryScale { get; set; } = 0.5;

    /// <summary>复制一份用于按作用域合并；属性都是值类型，浅拷贝即可。</summary>
    public ContextSettings Clone() => (ContextSettings)MemberwiseClone();
}
