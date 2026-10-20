using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把模型输出的 Markdown 转成可直接渲染的 HTML。
/// 只启用 Markdown 语法本身，禁用内联 HTML，避免模型输出里的 script / style 进入 WebView；
/// 链接只保留 http(s) / mailto，表格外面套一层可横向滚动的容器，长表格不会把消息区撑破。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()                   // GFM 表格
        .UseTaskLists()                    // - [x] 任务列表
        .UseEmphasisExtras()               // 删除线等
        .UseAutoLinks()                    // 裸 URL
        .UseSoftlineBreakAsHardlineBreak() // 模型常见的“一行一句”也能正确换行
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, Pipeline);
        SanitizeLinks(document);

        return Markdown.ToHtml(document, Pipeline)
            .Replace("<table>", "<div class=\"md-table\"><table>", StringComparison.Ordinal)
            .Replace("</table>", "</table></div>", StringComparison.Ordinal);
    }

    /// <summary>把 javascript: 之类的链接降级成页内锚点，避免点击时执行脚本。</summary>
    private static void SanitizeLinks(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url))
            {
                link.Url = "#";
            }
        }

        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeUrl(link.Url))
            {
                link.Url = "#";
            }
        }
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps ||
                string.Equals(uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase));
    }
}
