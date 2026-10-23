using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 小模型路由的传输层。与摘要传输分开，避免路由失败影响摘要，也方便单测直接注入假 JSON。
/// 实现（OpenAI 兼容）在 <c>SDemo.DesktopAgent.Agents</c>。
/// </summary>
public interface IRoutingChatTransport
{
    Task<string> CompleteAsync(
        ModelProvider provider,
        string modelId,
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
        int maxTokens,
        CancellationToken cancellationToken);
}
