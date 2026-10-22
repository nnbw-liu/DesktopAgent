namespace SDemo.DesktopAgent.Services;

/// <summary>审批的副作用类型。界面据此选择展示方式（文件内容 / 脚本 / 变更集）。</summary>
public enum ToolApprovalKind
{
    FileWrite,
    FileEdit,
    ProcessExecution,
    ApplyChanges
}

public enum ToolApprovalDecision
{
    Deny,
    ApproveOnce,
    ApproveForSession
}

/// <summary>
/// 一次审批请求。<see cref="Detail"/> 必须是用户真正要放行的内容原文：
/// 脚本不截断（超过 64KB 直接拒绝，不进审批），路径是校验后的路径。
/// </summary>
public sealed record ToolApprovalRequest(
    string ToolId,
    ToolApprovalKind Kind,
    string Title,
    string Summary,
    string Detail,
    AgentToolRisk Risk,
    bool AllowSessionRemember,
    string? SessionKey);

/// <summary>
/// 通用审批：同一时间只显示一个请求，其余排队；取消 / 停止生成时所有 pending 请求按拒绝处理。
/// 授权不持久化，应用重启后全部失效。
/// </summary>
public interface IToolApprovalService
{
    /// <summary>当前等待用户确认的请求；为空表示没有待确认项。</summary>
    ToolApprovalRequest? Current { get; }

    /// <summary>待确认状态变化（可能来自后台线程，订阅方需要自行切回 UI 线程）。</summary>
    event Action? Changed;

    Task<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>解决当前请求。没有待确认项时什么也不做。</summary>
    void Resolve(ToolApprovalDecision decision);

    /// <summary>拒绝所有等待中的请求（用户停止生成 / 应用退出）。</summary>
    void CancelAll();
}
