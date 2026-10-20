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
}
