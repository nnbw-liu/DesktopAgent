namespace SDemo.DesktopAgent.Sandbox;

/// <summary>页范围格式错误（<c>1-5,8</c> 约定）。两端都必须把它当成拒绝执行，而不是忽略。</summary>
public sealed class PdfRangeException : Exception
{
    public PdfRangeException(string message) : base(message)
    {
    }
}

/// <summary>
/// PDF 页范围的唯一解析入口：<c>1-5,8</c>，去重、升序、最多 <see cref="MaxPages"/> 页。
/// 空值表示默认前 <see cref="DefaultPages"/> 页。宿主工具与 SandboxHost 必须用同一套规则，
/// 否则审批里看到的页范围和实际解析的页会不一致。
/// </summary>
public static class PdfPageRange
{
    public const int MaxPages = 50;

    public const int DefaultPages = 5;

    public static IReadOnlyList<int> Parse(string? value, int pageCount)
    {
        if (!TryParse(value, pageCount, out var pages, out var error))
        {
            throw new PdfRangeException(error ?? "页范围格式不正确。");
        }

        return pages;
    }

    public static bool TryParse(
        string? value,
        int pageCount,
        out IReadOnlyList<int> pages,
        out string? error)
    {
        pages = [];
        error = null;

        if (pageCount <= 0)
        {
            error = "PDF 页数无效。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            var count = Math.Min(DefaultPages, pageCount);
            pages = Enumerable.Range(1, count).ToArray();
            return true;
        }

        var numbers = new SortedSet<int>();

        foreach (var rawSegment in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (rawSegment.Length == 0)
            {
                continue;
            }

            if (!TryParseSegment(rawSegment, out var first, out var last))
            {
                error = $"页范围格式不正确：{rawSegment}（示例：1-5,8）。";
                return false;
            }

            if (first < 1 || last < first)
            {
                error = $"页范围无效：{rawSegment}；页码范围从 1 开始，且起始页不能大于结束页。";
                return false;
            }

            if (last > pageCount)
            {
                error = $"页范围超出 PDF 实际页数（{last} > {pageCount}）。";
                return false;
            }

            for (var page = first; page <= last; page++)
            {
                numbers.Add(page);
            }

            if (numbers.Count > MaxPages)
            {
                error = $"单次最多读取 {MaxPages} 页，请缩小页范围。";
                return false;
            }
        }

        if (numbers.Count == 0)
        {
            error = "页范围为空。";
            return false;
        }

        pages = numbers.ToArray();
        return true;
    }

    /// <summary>只校验语法，不校验上界（宿主读到真实页数前用）。</summary>
    public static bool TryValidateSyntax(string? value, out string? error) =>
        TryParse(value, int.MaxValue, out _, out error);

    private static bool TryParseSegment(string segment, out int first, out int last)
    {
        first = 0;
        last = 0;

        var dash = segment.IndexOf('-');

        if (dash < 0)
        {
            return int.TryParse(segment, out var single) && (last = first = single) >= 0;
        }

        var startText = segment[..dash].Trim();
        var endText = segment[(dash + 1)..].Trim();

        // 只允许一个连字符：1-2-3 视为格式错误。
        if (startText.Contains('-') || endText.Contains('-'))
        {
            return false;
        }

        return int.TryParse(startText, out first) && int.TryParse(endText, out last);
    }
}
