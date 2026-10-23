namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 用户上传的附件。文件会复制到 AppData 下的 uploads 目录，
/// 数据库里只保存路径与元信息，重启后仍能显示。
/// </summary>
public sealed class ChatAttachment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string FileName { get; init; }

    public required string FilePath { get; init; }

    public long Size { get; init; }

    /// <summary>PDF 上传时自动提取的文本 sidecar（与 PDF 同目录的 <c>*.txt</c>）；没有则为 null。</summary>
    public string? ExtractedTextPath { get; init; }

    /// <summary>PDF 总页数；只有成功打开文档时才有值。</summary>
    public int? PageCount { get; init; }

    /// <summary>PDF 是否有文本层；扫描件为 false，模型不应该把“无文本”当成“无内容”。</summary>
    public bool? HasTextLayer { get; init; }
}
