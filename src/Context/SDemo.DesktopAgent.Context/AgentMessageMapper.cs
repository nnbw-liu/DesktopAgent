using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatRoleModel = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把领域消息渲染成「实际发给模型的文本」。这里是「消息里哪些东西会进入上下文」的唯一实现：
/// 读取正文、附件、工具轨迹，以及<b>用户显式「加入上下文」的派生卡片</b>。
/// 派生卡片默认不参与——译文 / 加工结果不该自动污染下一轮推理；只有
/// <see cref="DerivedArtifact.IncludeInContext"/> 为 true 的才追加。
/// 改这个文件前请先看 PluginDerivedContextTests。
/// 消息取舍（保留哪几条）由 <see cref="ContextTrimmer"/> 决定，这里只负责渲染。
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
        !string.IsNullOrWhiteSpace(message.Content)
        || message.Attachments.Count > 0
        || PromotedArtifacts(message).Any();

    /// <summary>兼容入口：全量内联附件 + 回放轨迹（测试与旧调用方使用）。</summary>
    public static AIChatMessage ToRequestMessage(ChatMessageModel message) => new(
        message.Role == ChatRoleModel.User ? ChatRole.User : ChatRole.Assistant,
        BuildContent(message, int.MaxValue, RenderTrace(message)).Text);

    /// <summary>ContextBuilder 使用的渲染入口；裁剪器通过委托调用它。</summary>
    public static RenderedContent Render(
        ChatMessageModel message,
        int attachmentCharLimit,
        string? toolTraceText) => BuildContent(message, attachmentCharLimit, toolTraceText);

    /// <summary>
    /// 渲染正文：<paramref name="attachmentCharLimit"/> 为 0 表示附件只保留「已省略」桩。
    /// 返回的 <see cref="RenderedContent.AttachmentBlock"/> / <see cref="RenderedContent.TraceText"/>
    /// 用于分段计量（附件、轨迹各占多少 token）。
    /// </summary>
    private static RenderedContent BuildContent(
        ChatMessageModel message,
        int attachmentCharLimit,
        string? toolTraceText)
    {
        var text = new StringBuilder(message.Content);
        string? attachmentBlock = null;
        string? traceBlock = null;

        foreach (var artifact in PromotedArtifacts(message))
        {
            if (text.Length > 0)
            {
                text.AppendLine().AppendLine();
            }

            text.Append("[插件结果 · ")
                .Append(artifact.PluginId)
                .AppendLine("]")
                .Append(artifact.Text);
        }

        if (message.Attachments.Count > 0)
        {
            var block = RenderAttachments(message.Attachments, attachmentCharLimit);

            if (block.Length > 0)
            {
                attachmentBlock = block.ToString();
                text.AppendLine().AppendLine().Append(attachmentBlock);
            }
        }

        if (!string.IsNullOrWhiteSpace(toolTraceText))
        {
            traceBlock = $"[工具轨迹]\n{toolTraceText}";
            text.AppendLine().AppendLine().Append(traceBlock);
        }

        return new RenderedContent(text.ToString(), attachmentBlock, traceBlock);
    }

    /// <summary>用户点过「加入上下文」、已完成且确有待发送文本的派生卡片。</summary>
    private static IEnumerable<DerivedArtifact> PromotedArtifacts(ChatMessageModel message) =>
        message.Derived.Where(artifact => artifact.ContributesToContext);

    private static StringBuilder RenderAttachments(IReadOnlyList<ChatAttachment> attachments, int charLimit)
    {
        var builder = new StringBuilder();

        // 全量入口（ToRequestMessage）仍然按 48K 总量封顶，避免一次读入巨型文件。
        var budget = charLimit <= 0 ? 0 : Math.Min(charLimit, AttachmentCharsTotal);

        foreach (var attachment in attachments)
        {
            builder.AppendLine().AppendLine();

            if (budget <= 0)
            {
                // 历史轮 / 超出内联上限：只留桩，路径保留，模型需要时可用工具重新读取。
                builder.Append("[附件已省略：")
                    .Append(attachment.FileName)
                    .Append(", ")
                    .Append(ByteSize.FormatSize(attachment.Size))
                    .Append(", ")
                    .Append(attachment.FilePath)
                    .Append(']');
                continue;
            }

            builder.Append("【附件：")
                .Append(attachment.FileName)
                .Append("（")
                .Append(ByteSize.FormatSize(attachment.Size))
                .Append("）】");

            var text = TryReadText(attachment, Math.Min(AttachmentCharsPerFile, budget));

            // PDF 的二进制本身读不出文本，但模型需要原始路径才能在后续调用 pdf_read 读取其他页。
            if (attachment.PageCount is int pageCount)
            {
                builder.AppendLine().Append($"（PDF 共 {pageCount} 页，文件路径：{attachment.FilePath}）");
            }

            // Office 自动提取有字符上限；把路径给模型，截断后才能用 office_read 按页 / 按表续读。
            if (IsOffice(attachment))
            {
                builder.AppendLine().Append(
                    $"（Office 文档，可用 office_read 按 slides / sheet / range 续读；文件路径：{attachment.FilePath}）");
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
                        : IsOffice(attachment)
                            ? $"（Office 文档未自动提取，可用 office_read 读取；路径：{attachment.FilePath}）"
                            : $"（二进制或无法读取的文件，路径：{attachment.FilePath}）");
                continue;
            }

            // Office 的 sidecar 本身就是 Markdown：按 markdown 围栏贴进去，标题 / 表格不会变成纯文本。
            builder.AppendLine().AppendLine(IsOffice(attachment) ? "```markdown" : "```text")
                .AppendLine(text).AppendLine("```");
            budget -= text.Length;
        }

        return builder;
    }

    private static bool IsPdf(ChatAttachment attachment) =>
        string.Equals(Path.GetExtension(attachment.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsOffice(ChatAttachment attachment)
    {
        var extension = Path.GetExtension(attachment.FilePath);
        return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase);
    }

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

    private static string? RenderTrace(ChatMessageModel message)
    {
        if (message.ToolTrace.Count == 0)
        {
            return null;
        }

        // 兼容入口没有预算概念：按最大条数渲染，单条仍然只保留摘要。
        var lines = message.ToolTrace
            .TakeLast(ToolTraceFormatter.MaxEntriesPerMessage)
            .Select(ToolTraceFormatter.RenderLine);

        return string.Join('\n', lines);
    }
}
