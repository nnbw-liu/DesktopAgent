namespace SDemo.DesktopAgent.Context;

/// <summary>P6 双模：完整工具面 / 精简工具面。</summary>
public enum AgentMode
{
    Full,
    Compact
}

/// <summary>用户偏好；Auto 时按模型档案推断。</summary>
public enum AgentModePreference
{
    Auto,
    Full,
    Compact
}
