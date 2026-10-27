using System.IO;
using Microsoft.Web.WebView2.Core;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// P4：WPF / WebView2 的隔离预览 host。把预览暂存目录映射到
/// <c>https://archify-preview.local/</c>，并拦截预览内产物导出的下载：
/// 只接受图片 / SVG / WebM，落到受控的 exports 目录，避免下载弹窗与路径不可控。
/// </summary>
public sealed class WebView2ArtifactPreviewHost : IArtifactPreviewHost
{
    private const string VirtualHost = "archify-preview.local";

    private readonly string _stagingRoot;
    private readonly string _exportsRoot;

    private CoreWebView2? _core;

    public WebView2ArtifactPreviewHost(IAppPaths paths)
    {
        _stagingRoot = Path.Combine(paths.DataRoot, "preview");
        _exportsRoot = Path.Combine(_stagingRoot, "exports");
    }

    public bool IsSupported => _core is not null;

    public Uri? BaseUri => _core is null ? null : new Uri($"https://{VirtualHost}/");

    public string? StagingRoot => _core is null ? null : _stagingRoot;

    public string? ExportsRoot => _core is null ? null : _exportsRoot;

    /// <summary>在 WebView2 初始化完成后调用；失败时保持不可用，UI 退回浏览器打开。</summary>
    public bool Attach(CoreWebView2 core)
    {
        if (_core is not null)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(_stagingRoot);
            Directory.CreateDirectory(_exportsRoot);
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                _stagingRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.DownloadStarting += OnDownloadStarting;
            _core = core;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            var suggested = Path.GetFileName(args.ResultFilePath);

            if (string.IsNullOrWhiteSpace(suggested))
            {
                suggested = Path.GetFileName(args.DownloadOperation.Uri);
            }

            if (!ArtifactExportPolicy.TryCreateTarget(
                    _exportsRoot,
                    suggested,
                    DateTimeOffset.Now,
                    out var target))
            {
                args.Cancel = true;
                args.Handled = true;
                return;
            }

            Directory.CreateDirectory(_exportsRoot);
            args.ResultFilePath = target!;
            args.Handled = true;
        }
        catch (Exception)
        {
            args.Cancel = true;
            args.Handled = true;
        }
    }
}
