namespace SDemo.DesktopAgent.Services;

/// <summary>
/// R1.4：把「真正弹给用户确认的审批」归到当前这一轮的 <see cref="AgentRunMetrics"/>。
/// <para>
/// 不改 <see cref="IToolApprovalService"/> 的签名：审批由工具包在工具线程里发起，
/// <see cref="AgentChatService"/> 在流式枚举期间开启作用域即可覆盖整次生成。
/// 评测里的 <c>AutoApprovals</c> 直接返回批准、不经过 <see cref="ToolApprovalService"/>，
/// 因此不会计入——这个数字只代表产品侧的人工确认成本。
/// </para>
/// </summary>
public static class ApprovalScope
{
    private static readonly AsyncLocal<AgentRunMetrics?> Current = new();

    /// <summary>开启一次生成的作用域；metrics 为 null 时是空操作（插件 / 旧路径）。</summary>
    public static IDisposable Begin(AgentRunMetrics? metrics)
    {
        var previous = Current.Value;
        Current.Value = metrics;
        return new Scope(previous);
    }

    /// <summary>审批请求已经上屏，记一次。</summary>
    public static void RecordPresented() => Current.Value?.RecordApproval();

    private sealed class Scope(AgentRunMetrics? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
