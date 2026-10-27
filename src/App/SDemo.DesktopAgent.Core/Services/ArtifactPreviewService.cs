using System.Security.Cryptography;
using System.Text;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// P4 隔离预览：把产物目录复制到宿主的暂存根目录，在副本的 <c>&lt;head&gt;</c> 里注入 CSP，
/// 再返回独立虚拟源 URL。工作区原件不动；iframe 只加载这份副本。
/// </summary>
public sealed class ArtifactPreviewService : IArtifactPreviewService
{
    // 预览只用于展示，50 MB 足够覆盖独立 HTML；超限时退回浏览器打开。
    private const long MaxArtifactBytes = 50 * 1024 * 1024;
    private const long MaxFolderBytes = 64 * 1024 * 1024;
    private const int MaxFolderFiles = 2000;

    private const string Csp =
        "default-src 'none'; "
        + "script-src 'unsafe-inline'; "
        + "style-src 'unsafe-inline'; "
        + "img-src data: blob:; "
        + "font-src data:; "
        + "media-src data: blob:; "
        + "connect-src 'none'; "
        + "frame-src 'none'; "
        + "worker-src 'none'; "
        + "navigate-to 'none'; "
        + "object-src 'none'; "
        + "base-uri 'none'; "
        + "form-action 'none'";

    private readonly IArtifactPreviewHost _host;

    public ArtifactPreviewService(IArtifactPreviewHost host)
    {
        _host = host;
    }

    public bool IsSupported =>
        _host.IsSupported
        && _host.BaseUri is not null
        && !string.IsNullOrWhiteSpace(_host.StagingRoot);

    public Task<ArtifactPreviewResult> PrepareAsync(
        string workspaceRoot,
        string artifactRelativePath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Prepare(workspaceRoot, artifactRelativePath, cancellationToken), cancellationToken);

    private ArtifactPreviewResult Prepare(
        string workspaceRoot,
        string artifactRelativePath,
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return ArtifactPreviewResult.Fail(
                "当前宿主不支持隔离预览；请用「用浏览器打开」。");
        }

        string source;

        try
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return ArtifactPreviewResult.Fail("当前会话没有工作区，无法预览产物。");
            }

            var root = Path.GetFullPath(workspaceRoot);
            var relative = NormalizeRelative(artifactRelativePath);

            if (relative is null)
            {
                return ArtifactPreviewResult.Fail("只允许预览工作区 .archify 下的 HTML 产物。");
            }

            source = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var artifactRoot = Path.TrimEndingDirectorySeparator(Path.Combine(root, ".archify"));

            if (!source.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactPreviewResult.Fail("只允许预览工作区 .archify 下的 HTML 产物。");
            }

            if (!File.Exists(source)
                || !source.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactPreviewResult.Fail("产物不存在，或不是 .html 文件。");
            }

            if (new FileInfo(source).Length > MaxArtifactBytes)
            {
                return ArtifactPreviewResult.Fail("产物超过 50 MB，请用浏览器打开。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return ArtifactPreviewResult.Fail($"无法读取产物：{ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stagingRoot = _host.StagingRoot!;
        var token = Guid.NewGuid().ToString("N");
        var stagedFolder = Path.Combine(stagingRoot, token);

        try
        {
            Directory.CreateDirectory(stagedFolder);
            var sourceFolder = Path.GetDirectoryName(source)!;
            var (files, bytes) = CopyFolder(sourceFolder, stagedFolder, cancellationToken);

            if (files == 0)
            {
                return ArtifactPreviewResult.Fail("产物目录为空，无法准备预览。");
            }

            if (bytes > MaxFolderBytes || files > MaxFolderFiles)
            {
                Directory.Delete(stagedFolder, recursive: true);
                return ArtifactPreviewResult.Fail("产物目录过大，请用浏览器打开。");
            }

            var stagedHtml = Path.Combine(
                stagedFolder,
                Path.GetRelativePath(sourceFolder, source));
            var injected = InjectCsp(stagedHtml);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
            var fileName = Uri.EscapeDataString(Path.GetFileName(stagedHtml));
            var previewUri = new Uri(_host.BaseUri!, $"{token}/{fileName}");

            return new ArtifactPreviewResult(
                true,
                injected
                    ? "隔离预览已准备：副本注入了 CSP，iframe 只允许 allow-scripts / allow-downloads。"
                    : "隔离预览已准备：产物自带 CSP，未重复注入。",
                previewUri,
                stagedFolder,
                source,
                injected,
                hash,
                new FileInfo(source).Length);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagedFolder);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            TryDelete(stagedFolder);
            return ArtifactPreviewResult.Fail($"准备预览失败：{ex.Message}");
        }
    }

    /// <summary>相对路径归一化；拒绝绝对路径、盘符、UNC 与任何 .. 段。</summary>
    private static string? NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim().Replace('\\', '/');

        if (normalized.StartsWith('/')
            || normalized.StartsWith('~')
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            return null;
        }

        var segments = normalized.Split('/');

        if (segments.Any(segment => segment is "" or ".." or "."))
        {
            return null;
        }

        return normalized;
    }

    private static (int Files, long Bytes) CopyFolder(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var files = 0;
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                     IgnoreInaccessible = false
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = new FileInfo(file).Length;

            // 先看上限再复制：避免超大目录在拒绝前把整个目录拷进暂存区。
            if (files + 1 > MaxFolderFiles || bytes + length > MaxFolderBytes)
            {
                return (files + 1, bytes + length);
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            files++;
            bytes += length;
        }

        return (files, bytes);
    }

    /// <summary>
    /// 在副本的 head 里插入 CSP。若产物已经自带 CSP，保持原样（不叠加第二条）。
    /// 不修改工作区原件。
    /// </summary>
    private static bool InjectCsp(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath);

        if (html.Contains("Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Csp}\">";
        var head = FindTag(html, "<head");

        if (head >= 0)
        {
            var end = html.IndexOf('>', head);
            html = end < 0
                ? meta + html
                : html.Insert(end + 1, Environment.NewLine + meta);
        }
        else
        {
            var htmlTag = FindTag(html, "<html");

            if (htmlTag < 0)
            {
                html = $"<head>{meta}</head>{html}";
            }
            else
            {
                var end = html.IndexOf('>', htmlTag);
                html = end < 0
                    ? $"<head>{meta}</head>{html}"
                    : html.Insert(end + 1, $"<head>{meta}</head>");
            }
        }

        File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
        return true;
    }

    /// <summary>只匹配完整标签名，避免把 &lt;header&gt; 当成 &lt;head&gt;。</summary>
    private static int FindTag(string html, string tag)
    {
        var index = 0;

        while (index < html.Length)
        {
            var found = html.IndexOf(tag, index, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return -1;
            }

            var next = found + tag.Length;

            if (next >= html.Length || html[next] == '>' || char.IsWhiteSpace(html[next]))
            {
                return found;
            }

            index = found + tag.Length;
        }

        return -1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暂存清理失败不影响用户；下次准备预览会写新目录。
        }
    }
}
