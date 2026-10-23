using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// OpenAI 兼容的小模型路由调用（M4）：temperature = 0、JSON Schema 约束、无工具、无历史。
/// 优先用 <see cref="ChatResponseFormat.ForJsonSchema"/>；端点不认结构化输出时退回 JSON 模式，
/// 再不行退回纯文本。之后的 Schema 校验与工具白名单仍由 <c>SmallModelRoutingAdvisor</c> 做。
/// </summary>
public sealed class OpenAiRoutingChatTransport : IRoutingChatTransport
{
    public async Task<string> CompleteAsync(
        ModelProvider provider,
        string modelId,
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
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

        using var document = JsonDocument.Parse(jsonSchema);
        var schema = document.RootElement.Clone();
        var maxOutput = Math.Max(64, maxTokens);

        try
        {
            var response = await client.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    Temperature = 0f,
                    MaxOutputTokens = maxOutput,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        schema,
                        "routing_advice",
                        "本轮工具路由建议")
                },
                cancellationToken).ConfigureAwait(false);

            return response.Text ?? string.Empty;
        }
        catch (ClientResultException ex) when (ex.Status is 400 or 404 or 422)
        {
            // 部分本地 / 兼容端点不支持 json_schema；退回 JSON 模式再试一次。
            var response = await client.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    Temperature = 0f,
                    MaxOutputTokens = maxOutput,
                    ResponseFormat = ChatResponseFormat.Json
                },
                cancellationToken).ConfigureAwait(false);

            return response.Text ?? string.Empty;
        }
    }
}
