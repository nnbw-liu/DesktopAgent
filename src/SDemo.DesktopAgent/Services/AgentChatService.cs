using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using SDemo.DesktopAgent.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatRoleModel = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.DesktopAgent.Services;

/// <summary>流式增量：正文与思维链分开返回。</summary>
public readonly record struct AgentDelta(string Text, string Reasoning);

/// <summary>
/// 通过 Microsoft Agent Framework 调用模型。只支持标准 OpenAI API，
/// provider 的 BaseUrl 直接作为 OpenAI 客户端的 Endpoint。
/// </summary>
public interface IAgentChatService
{
    /// <summary>流式生成回复；正文与思维链分别放在 <see cref="AgentDelta"/> 里。</summary>
    IAsyncEnumerable<AgentDelta> StreamAsync(
        ModelProvider provider,
        string modelId,
        IReadOnlyList<ChatMessageModel> history,
        IReadOnlySet<AgentSkill> skills,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentChatService : IAgentChatService
{
    private const string Instructions =
        """
        你是 SDemo DesktopAgent，一个运行在 Windows 桌面上的 AI 助手。
        回答保持简洁、直接，必要时给出可执行的步骤。默认使用简体中文。
        你可以使用用户开启的工具（文件读写、联网搜索、本地技能）；没有相应工具时不要假装能访问文件或网络。
        """;

    private readonly AgentToolbox _toolbox;

    public AgentChatService(AgentToolbox toolbox) => _toolbox = toolbox;

    public async IAsyncEnumerable<AgentDelta> StreamAsync(
        ModelProvider provider,
        string modelId,
        IReadOnlyList<ChatMessageModel> history,
        IReadOnlySet<AgentSkill> skills,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tools = _toolbox.Create(skills);
        var agent = CreateAgent(provider, modelId, tools);

        var messages = history
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => new AIChatMessage(
                message.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
                message.Content))
            .ToList();

        await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: cancellationToken))
        {
            var text = new StringBuilder();
            var reasoning = new StringBuilder();

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text: { Length: > 0 } chain }:
                        reasoning.Append(chain);
                        break;
                    case TextContent { Text: { Length: > 0 } chunk } when content is not TextReasoningContent:
                        text.Append(chunk);
                        break;
                }
            }

            if (text.Length > 0 || reasoning.Length > 0)
            {
                yield return new AgentDelta(text.ToString(), reasoning.ToString());
            }
        }
    }

    /// <summary>把 provider 配置映射成 MAF 的 AIAgent（底层是 OpenAI Chat Completion 客户端）。</summary>
    private static AIAgent CreateAgent(ModelProvider provider, string modelId, IList<AITool> tools)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl, UriKind.Absolute)
        };

        // 本地服务可能不需要 Key，但 OpenAI SDK 要求非空凭证。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(provider.ApiKey) ? "not-required" : provider.ApiKey);

        var chatClient = new OpenAIClient(credential, options).GetChatClient(modelId);

        return chatClient.AsAIAgent(
            instructions: Instructions,
            name: "SDemo Agent",
            tools: tools.Count == 0 ? null : tools);
    }
}
