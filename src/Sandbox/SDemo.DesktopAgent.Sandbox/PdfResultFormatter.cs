using System.Text;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把沙箱返回的 PDF 结果格式化成工具文本。
/// PDF 内容同样是“数据”：工具结果定界由 <c>AgentToolRegistry</c> 统一添加，这里只负责可读性。
/// </summary>
public static class PdfResultFormatter
{
    /// <summary>进入模型上下文的字符上限（沙箱内已按 maxChars 截断，这里再兜一次）。</summary>
    public const int MaxChars = PdfLimits.MaxChars;

    public static string FormatRead(string displayName, PdfExtraction extraction)
    {
        if (!extraction.Ok)
        {
            return Failure(extraction);
        }

        var builder = new StringBuilder();
        AppendHeader(builder, displayName, extraction);

        var pages = extraction.Pages
            .Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .ToArray();

        if (pages.Length == 0)
        {
            builder.AppendLine();
            builder.AppendLine("所选页未检测到文本层；当前未启用 OCR，无法读取扫描件内容。");
            return builder.ToString().TrimEnd();
        }

        foreach (var page in pages)
        {
            builder.AppendLine();
            AppendPage(builder, page);
        }

        if (extraction.Truncated)
        {
            builder.AppendLine();
            builder.AppendLine("（已截断）");
        }

        return Trim(builder.ToString()).TrimEnd();
    }

    public static string FormatInfo(string displayName, PdfExtraction extraction)
    {
        if (!extraction.Ok)
        {
            return Failure(extraction);
        }

        var builder = new StringBuilder();
        AppendHeader(builder, displayName, extraction, includePageRanges: false);

        if (!extraction.HasTextLayer)
        {
            builder.AppendLine();
            builder.AppendLine("前几页未检测到文本层；当前未启用 OCR，无法读取扫描件内容。");
        }

        return builder.ToString().TrimEnd();
    }

    private static string Failure(PdfExtraction extraction)
    {
        if (extraction.Encrypted)
        {
            return "PDF 已加密，当前不支持在对话中传密码。";
        }

        if (!string.IsNullOrWhiteSpace(extraction.Message))
        {
            return extraction.Message;
        }

        return "PDF 解析失败：未知原因。";
    }

    private static void AppendHeader(
        StringBuilder builder,
        string displayName,
        PdfExtraction extraction,
        bool includePageRanges = true)
    {
        builder.Append("PDF: ").AppendLine(displayName);
        builder.Append("页数: ").AppendLine(extraction.PageCount.ToString());
        builder.Append("加密: ").AppendLine(extraction.Encrypted ? "是" : "否");
        builder.Append("文本层: ").AppendLine(extraction.HasTextLayer ? "有" : "无");

        // pdf_info 的 Pages 只是抽样页，别让“页范围”看起来像用户请求的区间。
        if (includePageRanges && extraction.Pages.Count > 0)
        {
            builder.Append("页范围: ")
                .AppendLine(FormatPageRanges(extraction.Pages.Select(page => page.Number)));
        }
    }

    /// <summary>把 1,2,3,5 之类的页号显示成 1-3,5，方便模型知道这次读到了哪几页。</summary>
    private static string FormatPageRanges(IEnumerable<int> numbers)
    {
        var ordered = numbers.Distinct().Order().ToArray();
        var parts = new List<string>();
        var start = ordered[0];
        var previous = start;

        foreach (var number in ordered.Skip(1))
        {
            if (number == previous + 1)
            {
                previous = number;
                continue;
            }

            parts.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = number;
        }

        parts.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(",", parts);
    }

    private static void AppendPage(StringBuilder builder, PdfPageText page)
    {
        builder.Append("--- page ").Append(page.Number).AppendLine(" ---");
        builder.AppendLine(page.Text.TrimEnd());

        // 每页文本可能带大量空行；受 maxChars 限制，总长度已由沙箱兜底。
    }

    private static string Trim(string text) =>
        text.Length <= MaxChars
            ? text
            : string.Concat(text.AsSpan(0, MaxChars), "\n…（已截断）");
}
