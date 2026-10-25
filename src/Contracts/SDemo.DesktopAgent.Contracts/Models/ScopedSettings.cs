using System.Text.Json.Serialization;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;

namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 工作区 / 聊天室级别的设置覆盖。所有属性为 null 表示「未设置，继承上级」。
/// 解析顺序：聊天室 &gt; 工作区 &gt; 通用设置。
/// </summary>
public sealed class ScopedSettings
{
    /// <summary>本作用域使用的模型提供商；必须和 <see cref="ModelId"/> 同时存在。</summary>
    public string? ProviderId { get; set; }

    /// <summary>本作用域使用的模型 id；必须和 <see cref="ProviderId"/> 同时存在。</summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// 本作用域是否自动选模型：true = 按任务路由，false = 固定当前模型；
    /// null = 继承上级 / 全局路由开关。手选模型会显式写 false。
    /// </summary>
    public bool? AutoModelRouting { get; set; }

    /// <summary>是否启用上下文预算。</summary>
    public bool? ContextEnabled { get; set; }

    /// <summary>输入预算占可用窗口的比例。</summary>
    public double? BudgetRatio { get; set; }

    /// <summary>最近保留的完整轮数。</summary>
    public int? KeepRecentTurns { get; set; }

    /// <summary>超预算时是否自动压缩。</summary>
    public bool? AutoCompact { get; set; }

    /// <summary>是否把工作区结构 / 改动 / 动作放进每轮末尾的宿主上下文快照。</summary>
    public bool? StatePackEnabled { get; set; }

    /// <summary>是否在输入框下方显示上下文占用环。</summary>
    public bool? ShowContextMeter { get; set; }

    /// <summary>pin 消息数量上限。</summary>
    public int? MaxPinnedMessages { get; set; }

    /// <summary>Auto / Full / Compact 运行模式。</summary>
    public AgentModePreference? AgentMode { get; set; }

    /// <summary>是否提供 search_capabilities 兜底入口。</summary>
    public bool? SearchCapabilitiesEnabled { get; set; }

    /// <summary>是否把技能索引注入系统提示。</summary>
    public bool? SkillIndexEnabled { get; set; }

    /// <summary>是否启用工作区知识库。</summary>
    public bool? WorkspaceMemoryEnabled { get; set; }

    /// <summary>是否临时全量暴露工具。</summary>
    public bool? ExposeAllTools { get; set; }

    /// <summary>拒绝明显缩水的整文件覆盖。</summary>
    public bool? WriteFileShrinkGuard { get; set; }

    /// <summary>写入文件前是否要求确认。</summary>
    public bool? ConfirmFileWrite { get; set; }

    /// <summary>编辑前是否创建 checkpoint。</summary>
    public bool? CheckpointsEnabled { get; set; }

    /// <summary>是否允许白名单命令在宿主直跑。</summary>
    public bool? DirectRunEnabled { get; set; }

    /// <summary>复制一份，用于分支会话等需要独立修改的场景。</summary>
    public ScopedSettings Clone() => (ScopedSettings)MemberwiseClone();

    /// <summary>没有任何覆盖时返回 true，用于决定是否落库。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        ProviderId is null
        && ModelId is null
        && AutoModelRouting is null
        && ContextEnabled is null
        && BudgetRatio is null
        && KeepRecentTurns is null
        && AutoCompact is null
        && StatePackEnabled is null
        && ShowContextMeter is null
        && MaxPinnedMessages is null
        && AgentMode is null
        && SearchCapabilitiesEnabled is null
        && SkillIndexEnabled is null
        && WorkspaceMemoryEnabled is null
        && ExposeAllTools is null
        && WriteFileShrinkGuard is null
        && ConfirmFileWrite is null
        && CheckpointsEnabled is null
        && DirectRunEnabled is null;
}
