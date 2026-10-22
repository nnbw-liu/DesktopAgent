namespace SDemo.DesktopAgent.Sandbox;

/// <summary>PDF 相关的硬上限。宿主、工具、SandboxHost 共用一份，避免三处各写一个数字。</summary>
public static class PdfLimits
{
    /// <summary>单次读取的页数上限（<see cref="PdfPageRange.MaxPages"/> 的别名）。</summary>
    public const int MaxPagesPerRequest = PdfPageRange.MaxPages;

    /// <summary>单次提取的字符上限（job.json 的 maxChars 不得超过它）。</summary>
    public const int MaxChars = 40_000;

    /// <summary>未指定 maxChars 时的默认值。</summary>
    public const int DefaultChars = 20_000;

    /// <summary>上传附件的 PDF 大小上限（与 AttachmentStore.MaxFileSize 一致）。</summary>
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;

    /// <summary>工具读取的 PDF 大小上限；超过直接报错，不尝试解析。</summary>
    public const long MaxToolFileBytes = 50 * 1024 * 1024;

    /// <summary>上传附件时自动提取的页数（前 3 页）。</summary>
    public const int AttachmentPages = 3;

    /// <summary>上传附件时自动提取的字符上限。</summary>
    public const int AttachmentChars = 16 * 1024;

    /// <summary><c>pdf_info</c> 抽样判断文本层的页数。</summary>
    public const int InfoSamplePages = 3;
}
