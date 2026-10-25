using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次摘要请求的传输层输入（P2b §3.3.2 第二条：**复用热前缀**）。
/// <para>
/// 消息不是「一段拼好的文本」，而是**原对话的真前缀**：
/// <c>[system 会话原样] + [被遮蔽区域逐条回放] + [最后一条 user = 压缩指令]</c>。
/// 工具声明也按本轮请求原样带上——前缀一致，端点才可能命中之前那次请求的 KV 缓存。
/// </para>
/// </summary>
/// <param name="Messages">要发给模型的完整消息列表（最后一条是压缩指令）。</param>
/// <param name="Tools">本轮请求声明的工具；为空表示这次不声明工具（摘要不需要工具）。</param>
public sealed record SummaryChatRequest(
    ModelProvider Provider,
    string ModelId,
    IReadOnlyList<AIChatMessage> Messages,
    IReadOnlyList<AITool>? Tools,
    int MaxTokens);

/// <summary>
/// 摘要请求的传输层。抽出来是为了单测：假实现可以返回正常 / 超时 / 异常 / 格式不符的文本，
/// 不需要联网，也不需要真实密钥。实现（OpenAI 兼容）在 <c>SDemo.DesktopAgent.Agents</c>。
/// <para>
/// P2b：接口从契约层下移到上下文层——它现在要接 <see cref="AITool"/>（工具声明），
/// 而契约层按架构约定不允许引用任何 AI SDK。
/// </para>
/// </summary>
public interface ISummaryChatTransport
{
    /// <summary>
    /// 非流式补全。返回模型输出的正文；空串表示「没有文本输出」（例如模型直接走了工具调用），
    /// 由 <see cref="LlmContextSummarizer"/> 决定是否去掉工具重试或走兜底。
    /// </summary>
    Task<string> CompleteAsync(SummaryChatRequest request, CancellationToken cancellationToken);
}
