using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// OpenAI 兼容的非流式摘要调用（temperature = 0，无历史包袱，工具声明按调用方给的原样发送）。
/// <para>
/// P2b §3.3.2：消息列表是「会话原样 system + 被遮蔽区域 + 末尾压缩指令」，工具声明也照搬本轮请求，
/// 目的只有一个——让这次调用成为上一次请求的真前缀，命中端点的 KV / 前缀缓存。
/// 工具只声明、从不执行：这里是 <see cref="IChatClient.GetResponseAsync"/> 直连，没有工具循环。
/// </para>
/// </summary>
public sealed class OpenAiSummaryChatTransport : ISummaryChatTransport
{
    public async Task<string> CompleteAsync(
        SummaryChatRequest request,
        CancellationToken cancellationToken)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(request.Provider.BaseUrl, UriKind.Absolute)
        };

        // 本机服务可能不需要 Key，但 OpenAI SDK 要求非空凭证。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(request.Provider.ApiKey) ? "not-required" : request.Provider.ApiKey);

        var client = new OpenAIClient(credential, options)
            .GetChatClient(request.ModelId)
            .AsIChatClient();

        var chatOptions = new ChatOptions
        {
            Temperature = 0f,
            MaxOutputTokens = Math.Max(64, request.MaxTokens)
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            chatOptions.Tools = [.. tools];
        }

        var response = await client.GetResponseAsync(
            request.Messages,
            chatOptions,
            cancellationToken).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }
}
