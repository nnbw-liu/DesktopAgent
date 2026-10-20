using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Services;

/// <summary>流式增量：正文与思维链分开返回。</summary>
public readonly record struct AgentDelta(string Text, string Reasoning);

/// <summary>
/// 一次生成所需的全部输入：instructions / messages / tools / metrics。
/// 普通对话与插件 Run 都走 <see cref="IAgentChatService.RunAsync"/>，避免两套请求组装逻辑漂移。
/// </summary>
public sealed record AgentRunPlan(
    ModelProvider Provider,
    string ModelId,
    string Instructions,
    IReadOnlyList<ChatMessageModel> Messages,
    IReadOnlySet<AgentSkill> Tools,
    AgentRunMetrics? Metrics = null);

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
        string? skillName = null,
        AgentRunMetrics? metrics = null,
        CancellationToken cancellationToken = default);

    /// <summary>按现成的 plan 执行（插件 Run 用这个；普通对话由 <see cref="StreamAsync"/> 组装后调用）。</summary>
    IAsyncEnumerable<AgentDelta> RunAsync(
        AgentRunPlan plan,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentChatService : IAgentChatService
{
    private const string Instructions =
        """
        你是 SDemo DesktopAgent，一个运行在 Windows 桌面上的 AI 助手。
        回答保持简洁、直接，必要时给出可执行的步骤。默认使用简体中文。
        你可以使用系统提供的工具（工作区文件读写、联网搜索）；没有相应工具时不要假装能访问文件或网络。
        如果本轮提供了「技能」说明，请严格按照该技能的步骤和输出格式执行。
        """;

    private readonly AgentToolbox _toolbox;
    private readonly SkillCatalog _skills;

    public AgentChatService(AgentToolbox toolbox, SkillCatalog skills)
    {
        _toolbox = toolbox;
        _skills = skills;
    }

    public async IAsyncEnumerable<AgentDelta> StreamAsync(
        ModelProvider provider,
        string modelId,
        IReadOnlyList<ChatMessageModel> history,
        IReadOnlySet<AgentSkill> skills,
        string? skillName = null,
        AgentRunMetrics? metrics = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var plan = new AgentRunPlan(
            provider,
            modelId,
            BuildInstructions(skillName),
            history,
            skills,
            metrics);

        await foreach (var delta in RunAsync(plan, cancellationToken))
        {
            yield return delta;
        }
    }

    public async IAsyncEnumerable<AgentDelta> RunAsync(
        AgentRunPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tools = _toolbox.Create(plan.Tools, plan.Metrics);
        var agent = CreateAgent(plan.Provider, plan.ModelId, tools, plan.Instructions);

        var messages = plan.Messages
            .Where(AgentMessageMapper.ShouldInclude)
            .Select(AgentMessageMapper.ToRequestMessage)
            .ToList();

        await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: cancellationToken))
        {
            var text = new StringBuilder();
            var reasoning = new StringBuilder();

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case UsageContent { Details: { } usage }:
                        plan.Metrics?.AddUsage(usage);
                        break;
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

    /// <summary>本轮选了技能时，把技能全文追加到系统提示词，让模型按固定格式执行。</summary>
    private string BuildInstructions(string? skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return Instructions;
        }

        var skill = _skills.ReadContent(skillName);
        if (string.IsNullOrWhiteSpace(skill))
        {
            return Instructions;
        }

        return $"{Instructions}\n\n## 本轮使用的技能：{skillName}\n请严格按以下技能说明执行任务：\n\n{skill}";
    }

    /// <summary>把 provider 配置映射成 MAF 的 AIAgent（底层是 OpenAI Chat Completion 客户端）。</summary>
    private static AIAgent CreateAgent(ModelProvider provider, string modelId, IList<AITool> tools, string instructions)
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
            instructions: instructions,
            name: "SDemo Agent",
            tools: tools.Count == 0 ? null : tools);
    }
}
