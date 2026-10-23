using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 摘要请求的传输层。抽出来是为了单测：假实现可以返回正常 / 超时 / 异常 / 格式不符的文本，
/// 不需要联网，也不需要真实密钥。实现（OpenAI 兼容）在 <c>SDemo.DesktopAgent.Agents</c>。
/// </summary>
public interface ISummaryChatTransport
{
    Task<string> CompleteAsync(
        ModelProvider provider,
        string modelId,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken);
}
