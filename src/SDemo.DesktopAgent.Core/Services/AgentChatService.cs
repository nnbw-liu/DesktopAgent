using System.ClientModel;
using System.IO;
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
        string? skillName = null,
        AgentRunMetrics? metrics = null,
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

    private const int AttachmentCharsPerFile = 16 * 1024;
    private const int AttachmentCharsTotal = 48 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".csv", ".log",
        ".cs", ".csproj", ".sln", ".razor", ".js", ".jsx", ".ts", ".tsx", ".html", ".css",
        ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".sql", ".ps1", ".sh",
        ".bat", ".cmd", ".ini", ".toml", ".config", ".gradle", ".properties", ".vue"
    };

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
        var tools = _toolbox.Create(skills, metrics);
        var agent = CreateAgent(provider, modelId, tools, BuildInstructions(skillName));

        var messages = history
            .Where(message => !string.IsNullOrWhiteSpace(message.Content) || message.Attachments.Count > 0)
            .Select(message => new AIChatMessage(
                message.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
                BuildContent(message)))
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
                        metrics?.AddUsage(usage);
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

    /// <summary>把附件内容拼进发给模型的消息；文本文件内联，二进制文件只给元信息。</summary>
    private static string BuildContent(ChatMessageModel message)
    {
        if (message.Attachments.Count == 0)
        {
            return message.Content;
        }

        var builder = new StringBuilder(message.Content);
        var budget = AttachmentCharsTotal;

        foreach (var attachment in message.Attachments)
        {
            builder.AppendLine().AppendLine();
            builder.Append("【附件：")
                .Append(attachment.FileName)
                .Append("（")
                .Append(ByteSize.FormatSize(attachment.Size))
                .Append("）】");

            if (budget <= 0)
            {
                builder.AppendLine().Append("（附件内容较多，后续附件已省略）");
                continue;
            }

            var text = TryReadText(attachment, Math.Min(AttachmentCharsPerFile, budget));
            if (text is null)
            {
                builder.AppendLine().Append($"（二进制或无法读取的文件，路径：{attachment.FilePath}）");
                continue;
            }

            builder.AppendLine().AppendLine("```text").AppendLine(text).AppendLine("```");
            budget -= text.Length;
        }

        return builder.ToString();
    }

    private static string? TryReadText(ChatAttachment attachment, int maxChars)
    {
        if (!TextExtensions.Contains(Path.GetExtension(attachment.FilePath)))
        {
            return null;
        }

        try
        {
            if (!File.Exists(attachment.FilePath))
            {
                return null;
            }

            using var reader = new StreamReader(attachment.FilePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Min(maxChars, 8192)];
            var builder = new StringBuilder();

            while (builder.Length < maxChars)
            {
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - builder.Length));
                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
            }

            var text = builder.ToString();
            return text.Contains('\0') ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
