namespace SDemo.DesktopAgent.Sandbox;

/// <summary>PDF 单页文本。页号从 1 开始。</summary>
public sealed record PdfPageText(int Number, string Text);

/// <summary>
/// PDF 解析结果。Ok 为 false 时 <see cref="Message"/> 说明原因，
/// 调用方不得把“没有文本层 / 加密”当成“文件不存在”。
/// </summary>
public sealed record PdfExtraction(
    bool Ok,
    int PageCount,
    bool Encrypted,
    bool HasTextLayer,
    IReadOnlyList<PdfPageText> Pages,
    bool Truncated,
    string? Message);
