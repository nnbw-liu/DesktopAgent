using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Services;

/// <summary>工具的风险等级。高风险的执行类工具必须每次审批，且不允许“本会话默认批准”。</summary>
public enum AgentToolRisk
{
    Low,
    Medium,
    High
}

/// <summary>工具包里的一个函数。界面与审批层只认 Id / 风险 / 是否需要审批。</summary>
public sealed record AgentToolFunctionDescriptor(
    string Id,
    string Name,
    AgentToolRisk Risk = AgentToolRisk.Low,
    bool RequiresApproval = false);

/// <summary>
/// 工具包描述：输入框 chip、设置页开关与“本轮是否给模型”都由它驱动。
/// <see cref="DefaultEnabled"/> 只是新用户的默认值，实际状态存在 <c>AppSettings.Tools</c>。
/// </summary>
public sealed record AgentToolPackDescriptor(
    string Id,
    string Name,
    string Description,
    string Glyph,
    AgentToolRisk Risk,
    int Order,
    bool DefaultEnabled,
    bool WindowsOnly,
    IReadOnlyList<AgentToolFunctionDescriptor> Functions);

/// <summary>
/// 一次生成绑定的工具上下文。每次 Run 新建一份，避免单例工具包持有上一个会话的会话 / 工作区。
/// </summary>
public sealed record ToolContext(
    string SessionId,
    string WorkspaceRoot,
    IReadOnlyList<string> ReadOnlyRoots,
    ISettingsService Settings,
    IToolApprovalService Approvals,
    ISandboxJobService SandboxJobs,
    IToolAuditLog Audit);

/// <summary>
/// 工具包：搜索 / 文件 / PowerShell 都从这里进工具链。
/// 单例实现只保存描述与无状态依赖，<see cref="Create"/> 每次按 <see cref="ToolContext"/> 生成函数对象。
/// </summary>
public interface IAgentToolPack
{
    AgentToolPackDescriptor Descriptor { get; }

    IReadOnlyList<AITool> Create(ToolContext context);
}
