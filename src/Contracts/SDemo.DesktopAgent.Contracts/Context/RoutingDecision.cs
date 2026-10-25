namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一轮能力路由的结果（§4.3 / §4.4）。只描述「本轮把哪些工具交给模型、为什么」，
/// 不含任何工具实现，因此可以安全地放进 <see cref="ContextReport"/> 并渲染到界面上。
/// </summary>
/// <param name="ExposedTools">本轮真正交给模型的工具名（已去重、按暴露顺序）。</param>
/// <param name="CoreTools">其中来自核心集的部分（read_file / apply_patch 等）。</param>
/// <param name="PackTools">其中来自按需工具包的部分（pdf / powershell / 搜索等）。</param>
/// <param name="MaxExposedTools">本轮上限（8K 默认 6、32K 默认 12；全量暴露时等于可用工具数）。</param>
/// <param name="EstimatedSchemaTokens">工具 schema 的粗略 token 成本，用于预算预留；实际值在宿主侧量出后回填。</param>
/// <param name="EscapeHatch">本轮是否暴露了 <c>search_capabilities</c> 兜底入口。</param>
/// <param name="Truncated">可用工具多于上限、被截断时为 true。</param>
/// <param name="Reason">给用户 / 诊断看的短句：命中意图、模式与截断情况。</param>
public sealed record RoutingDecision(
    IReadOnlyList<string> ExposedTools,
    IReadOnlyList<string> CoreTools,
    IReadOnlyList<string> PackTools,
    int MaxExposedTools,
    int EstimatedSchemaTokens,
    bool EscapeHatch,
    bool Truncated,
    string Reason)
{
    /// <summary>规则路由（成本档 0，默认）。</summary>
    public const string RulesSource = "rules";

    /// <summary>小模型路由（成本档 1，M4）。</summary>
    public const string SmallModelSource = "small-model";

    /// <summary>
    /// 本轮决策来源：<see cref="RulesSource"/> / <see cref="SmallModelSource"/>。
    /// 模型不可用、超时或返回非法结果时仍回落规则，源保持 rules，并在 Reason 里注明原因。
    /// </summary>
    public string Source { get; init; } = RulesSource;

    /// <summary>
    /// 本轮类型（先于选工具的一步）：对话 / 问答 / 任务。
    /// <see cref="TurnIntent.Chat"/> 表示本轮不暴露工具，宿主改用对话提示词。
    /// </summary>
    public TurnIntent Intent { get; init; } = TurnIntent.Answer;

    /// <summary>
    /// 本轮所属的任务档（P1b §3.2.3）。档内工具集固定；相邻轮没有任务信号时沿用上一轮的档，
    /// 所以「继续」这种句子不会把工具面收掉一半。
    /// </summary>
    public ToolTaskProfile Profile { get; init; } = ToolTaskProfile.General;

    /// <summary>
    /// 任务档是否来自历史轮（本句没有任务信号）。界面据此说明「沿用了上一轮的任务档」。
    /// </summary>
    public bool ProfileInherited { get; init; }

    /// <summary>
    /// 本轮是否真的需要工具。为 false 时运行时收到空工具集，系统提示词换成「直接回答」，
    /// 宿主也不再因为「没有改文件」而追问。
    /// </summary>
    public bool NeedsTools => Intent != TurnIntent.Chat && ExposedTools.Count > 0;

    /// <summary>本轮没有可用工具（插件 Run / 用户全关工具包）。</summary>
    public static RoutingDecision Empty { get; } = new(
        [],
        [],
        [],
        MaxExposedTools: 0,
        EstimatedSchemaTokens: 0,
        EscapeHatch: false,
        Truncated: false,
        Reason: "本轮未启用工具");

    /// <summary>暴露的工具数。</summary>
    public int Count => ExposedTools.Count;

    /// <summary>工具是否在本轮暴露（大小写不敏感，工具名本身是小写）。</summary>
    public bool Exposes(string toolName) =>
        ExposedTools.Contains(toolName, StringComparer.Ordinal);
}
