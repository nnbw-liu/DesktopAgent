namespace SDemo.DesktopAgent.Services;

/// <summary>工具的风险等级。高风险的执行类工具必须每次审批，且不允许“本会话默认批准”。</summary>
public enum AgentToolRisk
{
    Low,
    Medium,
    High
}
