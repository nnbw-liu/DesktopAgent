namespace SDemo.DesktopAgent.Context;

/// <summary>错误分类：UI 据此决定是普通失败提示，还是「上下文超限 / 需要压缩」的专门提示。</summary>
public enum AgentErrorKind
{
    Other,
    /// <summary>服务端返回 400，且重试后仍超限。</summary>
    ContextOverflow,
    /// <summary>P1 的确定性裁剪已到极限，需要 P2 的摘要压缩。</summary>
    NeedsCompaction
}

/// <summary>
/// 上下文相关的用户可见异常。消息本身就是给用户看的说明，UI 不再拼接原始 JSON。
/// </summary>
public sealed class ContextBudgetException : Exception
{
    public ContextBudgetException(AgentErrorKind kind, string message, ContextReport? report)
        : base(message)
    {
        Kind = kind;
        Report = report;
    }

    public AgentErrorKind Kind { get; }

    public ContextReport? Report { get; }
}
