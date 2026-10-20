using System.IO;
using Microsoft.AspNetCore.Components.Forms;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 上传附件的落盘位置：{AppData}\uploads\{sessionId}\。
/// 数据库只存路径，文件本身留在这里，重启后仍可读取 / 显示。
/// 放在 UI 工程是因为它只处理 Blazor 的 <see cref="IBrowserFile"/>。
/// </summary>
public sealed class AttachmentStore
{
    public const long MaxFileSize = 25 * 1024 * 1024;

    private readonly string _root;

    public AttachmentStore(IAppPaths paths)
        : this(paths.UploadsRoot)
    {
    }

    public AttachmentStore(string root)
    {
        _root = root;
    }

    public async Task<ChatAttachment> SaveAsync(
        IBrowserFile file,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var folder = string.IsNullOrWhiteSpace(sessionId) ? "draft" : sessionId;
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(file.Name);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, id + Path.GetExtension(fileName));

        await using (var input = file.OpenReadStream(MaxFileSize, cancellationToken))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        return new ChatAttachment
        {
            Id = id,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "附件" : fileName,
            FilePath = path,
            Size = file.Size
        };
    }

    public void Delete(ChatAttachment attachment)
    {
        try
        {
            if (File.Exists(attachment.FilePath))
            {
                File.Delete(attachment.FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着，不影响主流程。
        }
    }

    /// <summary>1.2 KB / 3.4 MB 这类短文本。</summary>
    public static string FormatSize(long bytes) => ByteSize.FormatSize(bytes);
}
