using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>P4：预览面板只装 iframe，sandbox 不给 same-origin；复核与导出入口可操作。</summary>
public sealed class ArtifactPreviewPanelTests : TestContext
{
    [Fact]
    public void 渲染隔离_iframe_且不内联产物_HTML()
    {
        var cut = RenderPanel();

        var frame = cut.Find("iframe");
        Assert.Equal("allow-scripts allow-downloads", frame.GetAttribute("sandbox"));
        Assert.DoesNotContain("allow-same-origin", frame.GetAttribute("sandbox"));
        Assert.Contains("archify-preview.test", frame.GetAttribute("src"));
        Assert.DoesNotContain("<h1>artifact</h1>", cut.Markup);
        Assert.Contains("CSP 已注入", cut.Markup);
    }

    [Fact]
    public void 点击视觉复核展示_pending_与截图入口()
    {
        var cut = RenderPanel();

        FindButton(cut, "视觉复核").Click();

        cut.WaitForAssertion(() => Assert.Contains("visualReview=pending", cut.Markup));
        Assert.Contains("自动截图不等于人工视觉复核", cut.Markup);
        Assert.Contains("打开 1440×900 浅色", cut.Markup);
        Assert.Equal(".archify/order/order.html", Services.GetRequiredService<StubArchifyReviewService>().LastArtifact);
    }

    [Fact]
    public void 在浏览器打开用原产物绝对路径()
    {
        var cut = RenderPanel();
        var launcher = Services.GetRequiredService<RecordingFileLauncherService>();

        FindButton(cut, "在浏览器打开").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "C:\\work\\.archify\\order\\order.html",
            Assert.Single(launcher.Opened)));
    }

    [Fact]
    public void 打开导出目录走环境服务()
    {
        var cut = RenderPanel();
        var environment = Services.GetRequiredService<StubAppEnvironmentService>();

        FindButton(cut, "打开导出目录").Click();

        Assert.Equal("C:\\staging\\exports", Assert.Single(environment.Folders));
    }

    private IRenderedComponent<ArtifactPreviewPanel> RenderPanel()
    {
        var launcher = new RecordingFileLauncherService();
        var review = new StubArchifyReviewService();
        var environment = new StubAppEnvironmentService();

        Services.AddSingleton(launcher);
        Services.AddSingleton<IFileLauncherService>(launcher);
        Services.AddSingleton<IArtifactPreviewHost, StubArtifactPreviewHost>();
        Services.AddSingleton(review);
        Services.AddSingleton<IArchifyReviewService>(review);
        Services.AddSingleton(environment);
        Services.AddSingleton<IAppEnvironmentService>(environment);

        return RenderComponent<ArtifactPreviewPanel>(parameters => parameters
            .Add(component => component.Preview, new ArtifactPreviewResult(
                true,
                "预览已准备。",
                new Uri("https://archify-preview.test/token/order.html"),
                "C:\\staging\\token",
                "C:\\work\\.archify\\order\\order.html",
                true,
                "abc",
                123))
            .Add(component => component.WorkspaceRoot, "C:\\work")
            .Add(component => component.ArtifactRelativePath, ".archify/order/order.html")
            .Add(component => component.ArtifactFullPath, "C:\\work\\.archify\\order\\order.html"));
    }

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<ArtifactPreviewPanel> cut, string text) =>
        cut.FindAll("button").First(button => button.TextContent.Contains(text, StringComparison.Ordinal));
}
