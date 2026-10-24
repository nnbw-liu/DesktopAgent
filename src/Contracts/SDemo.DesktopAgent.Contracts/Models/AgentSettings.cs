namespace SDemo.DesktopAgent.Models;

/// <summary>P6 小模型 / 工具面设置。</summary>
public sealed class AgentSettings
{
    /// <summary>8K 窗口的默认暴露工具数（核心集 + 按需包，§4.4）。</summary>
    public const int SmallWindowMaxExposedTools = 6;

    /// <summary>32K 窗口的默认暴露工具数。</summary>
    public const int LargeWindowMaxExposedTools = 12;

    /// <summary>默认模式：Auto / Full / Compact。</summary>
    public Context.AgentModePreference Mode { get; set; } = Context.AgentModePreference.Auto;

    /// <summary>Compact 模式每轮最多函数数（默认 4）。</summary>
    public int CompactMaxTools { get; set; } = 4;

    /// <summary>
    /// 每轮最多暴露给模型的工具数（M1）。0 = 按窗口自动：8K 6 个、32K 12 个；
    /// &gt;0 时手动覆盖（夹在 1–24）。Compact 仍受 <see cref="CompactMaxTools"/> 二次夹紧。
    /// </summary>
    public int MaxExposedTools { get; set; }

    /// <summary>
    /// 临时切回「全量暴露」：模型与旧版一样看到所有启用工具。风险兜底用，
    /// 工具段仍受窗口预算（8K ≤1.5K token）约束。
    /// </summary>
    public bool ExposeAllTools { get; set; }

    /// <summary>本轮是否提供 <c>search_capabilities</c> 兜底入口（使用工具时默认开启）。</summary>
    public bool SearchCapabilitiesEnabled { get; set; } = true;

    /// <summary>M2：是否把技能索引注入系统提示（正文仍由 <c>load_skill</c> 按需读取）。</summary>
    public bool SkillIndexEnabled { get; set; } = true;

    /// <summary>索引最多列几条技能（3–5 条通常就够；默认 5）。</summary>
    public int SkillIndexMaxEntries { get; set; } = 5;

    /// <summary>
    /// M3：是否启用工作区知识库。关闭时不注入索引行、不暴露 memory 工具，
    /// 上下文成本严格为 0；打开时只多一句「本工作区有 N 条归档会话」。
    /// </summary>
    public bool WorkspaceMemoryEnabled { get; set; } = true;

    /// <summary>单次检索默认返回几条命中（1–5；结果还要受 800 token 预算约束）。</summary>
    public int WorkspaceMemoryMaxHits { get; set; } = 5;

    /// <summary>
    /// M4：规则不确定时是否让小模型做一次结构化路由（成本档 1）。
    /// Compact 永远只用规则；模型不可用 / 超时 / JSON 不合规时自动回落规则，不阻塞对话。
    /// </summary>
    public bool SmallModelRoutingEnabled { get; set; } = true;

    /// <summary>路由模型 provider id；为空时复用摘要模型，再退到当前会话模型。</summary>
    public string? RoutingProviderId { get; set; }

    /// <summary>路由模型 id；为空时复用摘要模型，再退到当前会话模型。</summary>
    public string? RoutingModelId { get; set; }

    /// <summary>小模型路由超时（秒）；超时直接回落规则。</summary>
    public int RoutingTimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// P7：拒绝“明显缩水”的 write_file 覆盖（默认开启）。模型补丁失败后常整文件重写，
    /// 只写回自己记得的片段会把源文件截断；关闭后允许合法的大段删减。
    /// </summary>
    public bool WriteFileShrinkGuard { get; set; } = true;

    /// <summary>
    /// Compact 模式最大工具迭代（默认 8）。P6 实测 6 次会在「读两次 + 补丁失败重试」时
    /// 提前耗尽，模型来不及换 replace_text / write_file；循环安全由 FICC 轮数上限兜底。
    /// </summary>
    public int CompactMaxToolCalls { get; set; } = 8;

    /// <summary>
    /// Full 模式每轮最大工具迭代（默认 8）。P7 外部仓库任务步数更长，允许在评测配置里放宽；
    /// 产品默认保持不变，循环仍由 FICC 轮数上限兜底。
    /// </summary>
    public int FullMaxToolCalls { get; set; } = 8;

    /// <summary>
    /// P1a：工具循环由宿主自己跑（<c>NativeAgentLoop</c>）而不是 MAF 的 FICC。
    /// <para>
    /// 打开后每次模型请求都是显式的 step：工具调用与结果作为真实的 <c>tool_calls</c> /
    /// <c>tool</c> 消息回灌，界面上能按「第几步」归组。默认关闭——按迁移文档 §6.1，
    /// 先用 <c>tools/AgentEval</c> 对比新旧两条路径，再决定是否改成默认。
    /// </para>
    /// </summary>
    public bool NativeLoop { get; set; }

    /// <summary>复制一份用于按作用域合并；属性都是值类型，浅拷贝即可。</summary>
    public AgentSettings Clone() => (AgentSettings)MemberwiseClone();

    /// <summary>按窗口解析本轮暴露上限；<paramref name="contextWindow"/> ≤0 视为未知（按 32K 档）。</summary>
    public int ResolveMaxExposedTools(int contextWindow)
    {
        if (ExposeAllTools)
        {
            return int.MaxValue;
        }

        if (MaxExposedTools > 0)
        {
            return Math.Clamp(MaxExposedTools, 1, 24);
        }

        return contextWindow > 0 && contextWindow <= 8192
            ? SmallWindowMaxExposedTools
            : LargeWindowMaxExposedTools;
    }

    /// <summary>
    /// 再叠加模式上限：Compact 沿用 P6 的 <see cref="CompactMaxTools"/>，
    /// 因此用户在设置里调到 3 时仍生效；全量暴露优先于模式。
    /// </summary>
    public int ResolveMaxExposedTools(int contextWindow, Context.AgentMode mode)
    {
        var windowLimit = ResolveMaxExposedTools(contextWindow);

        return mode == Context.AgentMode.Compact && !ExposeAllTools
            ? Math.Min(windowLimit, Math.Clamp(CompactMaxTools, 1, 16))
            : windowLimit;
    }
}
