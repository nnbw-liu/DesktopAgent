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

            // PDF 的二进制本身读不出文本，但模型需要原始路径才能在后续调用 pdf_read 读取其他页。
            if (attachment.PageCount is int pageCount)
            {
                builder.AppendLine().Append($"（PDF 共 {pageCount} 页，文件路径：{attachment.FilePath}）");
            }

            if (attachment.HasTextLayer is false)
            {
                builder.AppendLine().Append("（未检测到文本层；当前未启用 OCR，无法读取扫描件内容）");
                continue;
            }

            if (text is null)
            {
                builder.AppendLine().Append(
                    IsPdf(attachment)
                        ? $"（PDF 文本提取未完成，可用 pdf_read 工具重试；路径：{attachment.FilePath}）"
                        : $"（二进制或无法读取的文件，路径：{attachment.FilePath}）");
                continue;
            }

            builder.AppendLine().AppendLine("```text").AppendLine(text).AppendLine("```");
            budget -= text.Length;
        }

        return builder.ToString();
    }

    private static bool IsPdf(ChatAttachment attachment) =>
        string.Equals(Path.GetExtension(attachment.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadText(ChatAttachment attachment, int maxChars)
    {
        // PDF 上传时已由沙箱提取前几页到 sidecar；优先读它，避免把二进制 PDF 当“无法读取”。
        if (!string.IsNullOrWhiteSpace(attachment.ExtractedTextPath))
        {
            var extracted = ReadLimitedText(attachment.ExtractedTextPath, maxChars);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        if (!TextExtensions.Contains(Path.GetExtension(attachment.FilePath)))
        {
            return null;
        }

        return ReadLimitedText(attachment.FilePath, maxChars);
    }

    private static string? ReadLimitedText(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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
