using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Agents;

/// <summary>
/// 一次模型调用的全部输入。只使用 provider 中立类型（<see cref="ChatMessage"/> / <see cref="AITool"/>），
/// 上层（应用编排）负责决定发什么、用什么预算；本层只管「怎么发」。
/// </summary>
public sealed record AgentInvocation(
    Uri Endpoint,
    string ApiKey,
    string ModelId,
    string Instructions,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AITool> Tools,
    int MaxToolIterations,
    string AgentName = "SDemo Agent",
    Func<IChatClient, IChatClient>? Decorate = null,
    // 思考强度（low / medium / high）；空表示按服务端默认。
    string? ReasoningEffort = null,
    // P1a：工具循环由谁跑。false = MAF 的 FunctionInvokingChatClient（现状）；true = 内置 AgentLoop。
    bool NativeLoop = false);

/// <summary>一次流式调用里，模型侧产生的增量事件。上层据此更新 UI / 统计 / 轨迹。</summary>
public abstract record AgentStreamEvent
{
    /// <summary>正文增量。</summary>
    public sealed record Text(string Value) : AgentStreamEvent;

    /// <summary>思维链增量（部分模型返回）。</summary>
    public sealed record Reasoning(string Value) : AgentStreamEvent;

    /// <summary>本轮请求的服务端用量（可能多次回调）。</summary>
    public sealed record Usage(int InputTokens, int OutputTokens) : AgentStreamEvent;

    /// <summary>
    /// 一次模型请求（DSH 的 step）开始。只有内置循环会发；FICC 的轮次对宿主不可见。
    /// </summary>
    /// <param name="Step">第几次请求（0 起）。</param>
    /// <param name="MaxSteps">本次调用最多几次请求。</param>
    /// <param name="At">开始时间。</param>
    public sealed record StepStarted(int Step, int MaxSteps, DateTimeOffset At) : AgentStreamEvent;

    /// <summary>一次模型请求结束（含它激发的工具）。</summary>
    public sealed record StepCompleted(int Step, DateTimeOffset At) : AgentStreamEvent;

    /// <summary>模型请求执行一个工具。</summary>
    /// <param name="CallId">调用 id；tool/result 与它配对。</param>
    /// <param name="Name">工具名。</param>
    /// <param name="Arguments">解析后的参数；参数不是合法 JSON 时为空。</param>
    /// <param name="Step">这是第几步发起的调用。</param>
    public sealed record FunctionCall(
        string CallId,
        string Name,
        IDictionary<string, object?>? Arguments,
        int Step = 0) : AgentStreamEvent;

    /// <summary>工具执行结果（由运行时回填进工具历史）。</summary>
    public sealed record FunctionResult(string CallId, string? Result, int Step = 0) : AgentStreamEvent;
}

/// <summary>
/// 模型运行时。实现可以是 MAF、直连 HTTP、假数据；上层只依赖这个接口，
/// 因此可以在没有网络与密钥的情况下测试编排逻辑。
/// </summary>
public interface IAgentRuntime
{
    IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentInvocation invocation,
        CancellationToken cancellationToken = default);
}
