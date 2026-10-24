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
    bool NativeLoop = false,
    // P1b：随某次工具调用的结果，在**本轮内**补一批工具（search_capabilities 的闭环）。
    // 返回空表示不补；宿主保证最多补一次，且不新增用户轮次。
    Func<AgentToolCallRequest, IReadOnlyList<AITool>>? ExpandTools = null);

/// <summary>一次工具调用（供 <see cref="AgentInvocation.ExpandTools"/> 判断要不要补工具）。</summary>
/// <param name="ToolName">模型调用的工具名。</param>
/// <param name="Arguments">参数（可能为空）。</param>
/// <param name="Result">工具返回的文本结果。</param>
/// <param name="Step">这是第几步发起的调用。</param>
public sealed record AgentToolCallRequest(
    string ToolName,
    IDictionary<string, object?>? Arguments,
    string? Result,
    int Step);

/// <summary>一次流式调用里，模型侧产生的增量事件。上层据此更新 UI / 统计 / 轨迹。</summary>
public abstract record AgentStreamEvent
{
    /// <summary>正文增量。</summary>
    public sealed record Text(string Value) : AgentStreamEvent;

    /// <summary>思维链增量（部分模型返回）。</summary>
    public sealed record Reasoning(string Value) : AgentStreamEvent;

    /// <summary>
    /// 本轮请求的服务端用量（可能多次回调）。
    /// </summary>
    /// <param name="InputTokens">输入 token。</param>
    /// <param name="OutputTokens">输出 token。</param>
    /// <param name="CacheHitTokens">
    /// P2a：命中端点上文缓存的输入 token（<c>UsageDetails.CachedInputTokenCount</c>）。
    /// 端点不报就是 0；界面据此决定显不显示命中率，不给假数字。
    /// </param>
    public sealed record Usage(int InputTokens, int OutputTokens, int CacheHitTokens = 0) : AgentStreamEvent;

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

    /// <summary>
    /// 本轮内补了一批工具（P1b：<c>search_capabilities</c> 的闭环）。
    /// 下一步请求就会带上它们；界面上落成一行「宿主提示」，让用户看得见为什么突然多了一个工具。
    /// </summary>
    /// <param name="Step">在第几步之后补的。</param>
    /// <param name="Tools">补进来的工具名。</param>
    /// <param name="Reason">给用户看的一句话。</param>
    public sealed record ToolsExpanded(int Step, IReadOnlyList<string> Tools, string Reason) : AgentStreamEvent;
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
