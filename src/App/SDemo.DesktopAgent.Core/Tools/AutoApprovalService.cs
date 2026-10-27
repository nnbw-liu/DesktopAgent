namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 「完全权限」下的审批门：直接给出「批准一次」，不把请求交给界面。
/// <para>
/// 由 <c>AgentChatService</c> 在构造 <see cref="ToolContext"/> 时按聊天室的作用域包一层，
/// 所以文件写入、PowerShell、Office、技能脚本这些确认框走的是同一个开关，
/// 不需要每个工具包各写一遍判断。取消令牌一旦触发仍然按拒绝处理。
/// </para>
/// <para>
/// 只影响审批：沙箱是否使用由 <c>EffectiveSettings.Sandbox</c> 单独决定，
/// 两者互不替代——只关沙箱但保留审批，确认框仍会弹出。
/// </para>
/// </summary>
public sealed class AutoApprovalService(IToolApprovalService inner) : IToolApprovalService
{
    private readonly IToolApprovalService _inner = inner;

    /// <summary>自动批准不会产生待确认项，因此这里始终是内部的当前请求（正常为空）。</summary>
    public ToolApprovalRequest? Current => _inner.Current;

    public event Action? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public Task<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 已经取消 / 停止生成的调用不能因为「免审批」就继续执行。
        return Task.FromResult(cancellationToken.IsCancellationRequested
            ? ToolApprovalDecision.Deny
            : ToolApprovalDecision.ApproveOnce);
    }

    public void Resolve(ToolApprovalDecision decision) => _inner.Resolve(decision);

    public void CancelAll() => _inner.CancelAll();
}
