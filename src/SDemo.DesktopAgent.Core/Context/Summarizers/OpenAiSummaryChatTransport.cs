using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 摘要请求的传输层。抽出来是为了单测：假实现可以返回正常 / 超时 / 异常 / 格式不符的文本，
/// 不需要联网，也不需要真实密钥。
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

/// <summary>OpenAI 兼容的非流式摘要调用（temperature = 0，无工具、无历史）。</summary>
public sealed class OpenAiSummaryChatTransport : ISummaryChatTransport
{
    public async Task<string> CompleteAsync(
        ModelProvider provider,
        string modelId,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl, UriKind.Absolute)
        };

        // 本机服务可能不需要 Key，但 OpenAI SDK 要求非空凭证。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(provider.ApiKey) ? "not-required" : provider.ApiKey);

        var client = new OpenAIClient(credential, options)
            .GetChatClient(modelId)
            .AsIChatClient();

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await client.GetResponseAsync(
            messages,
            new ChatOptions
            {
                Temperature = 0f,
                MaxOutputTokens = Math.Max(64, maxTokens)
            },
            cancellationToken).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }
}
