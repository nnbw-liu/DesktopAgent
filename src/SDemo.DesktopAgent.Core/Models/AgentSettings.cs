namespace SDemo.DesktopAgent.Models;

/// <summary>P6 小模型 / 工具面设置。</summary>
public sealed class AgentSettings
{
    /// <summary>默认模式：Auto / Full / Compact。</summary>
    public Context.AgentModePreference Mode { get; set; } = Context.AgentModePreference.Auto;

    /// <summary>Compact 模式每轮最多函数数（默认 4）。</summary>
    public int CompactMaxTools { get; set; } = 4;

    /// <summary>Compact 模式最大工具迭代（默认 6）。</summary>
    public int CompactMaxToolCalls { get; set; } = 6;
}
