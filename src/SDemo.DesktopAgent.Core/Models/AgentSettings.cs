namespace SDemo.DesktopAgent.Models;

/// <summary>P6 小模型 / 工具面设置。</summary>
public sealed class AgentSettings
{
    /// <summary>默认模式：Auto / Full / Compact。</summary>
    public Context.AgentModePreference Mode { get; set; } = Context.AgentModePreference.Auto;

    /// <summary>Compact 模式每轮最多函数数（默认 4）。</summary>
    public int CompactMaxTools { get; set; } = 4;

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
}
