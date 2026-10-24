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
    string? ReasoningEffort = null);

/// <summary>一次流式调用里，模型侧产生的增量事件。上层据此更新 UI / 统计 / 轨迹。</summary>
public abstract record AgentStreamEvent
{
    /// <summary>正文增量。</summary>
    public sealed record Text(string Value) : AgentStreamEvent;

    /// <summary>思维链增量（部分模型返回）。</summary>
    public sealed record Reasoning(string Value) : AgentStreamEvent;

    /// <summary>本轮请求的服务端用量（可能多次回调）。</summary>
    public sealed record Usage(int InputTokens, int OutputTokens) : AgentStreamEvent;

    /// <summary>模型请求执行一个工具。</summary>
    public sealed record FunctionCall(string CallId, string Name, IDictionary<string, object?>? Arguments) : AgentStreamEvent;

    /// <summary>工具执行结果（由 FunctionInvokingChatClient 回填）。</summary>
    public sealed record FunctionResult(string CallId, string? Result) : AgentStreamEvent;
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
