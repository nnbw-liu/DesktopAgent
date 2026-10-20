using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatRoleModel = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把领域消息映射成发给模型的消息。这里是「消息里哪些东西会进入上下文」的唯一入口：
/// 只读 <see cref="ChatMessageModel.Content"/> 与附件，<b>派生卡片（Derived）永远不参与</b>——
/// 译文不该污染下一轮推理。改这个文件前请先看 PluginDerivedContextTests。
/// </summary>
internal static class AgentMessageMapper
{
    private const int AttachmentCharsPerFile = 16 * 1024;
    private const int AttachmentCharsTotal = 48 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".csv", ".log",
        ".cs", ".csproj", ".sln", ".razor", ".js", ".jsx", ".ts", ".tsx", ".html", ".css",
        ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".sql", ".ps1", ".sh",
        ".bat", ".cmd", ".ini", ".toml", ".config", ".gradle", ".properties", ".vue"
    };

    public static bool ShouldInclude(ChatMessageModel message) =>
        !string.IsNullOrWhiteSpace(message.Content) || message.Attachments.Count > 0;

    public static AIChatMessage ToRequestMessage(ChatMessageModel message) => new(
        message.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
        BuildContent(message));

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
}
