namespace SDemo.DesktopAgent.Services;

/// <summary>非 WebView2 宿主的预览兜底：不映射虚拟源，只保留“用浏览器打开”。</summary>
public sealed class UnavailableArtifactPreviewHost : IArtifactPreviewHost
{
    public bool IsSupported => false;

    public Uri? BaseUri => null;

    public string? StagingRoot => null;

    public string? ExportsRoot => null;
}
