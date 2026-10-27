using System.IO;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>P3：产物按钮只把工作区 .archify 下真实存在的文件交给宿主打开。</summary>
public sealed class ArtifactLinksTests : TestContext
{
    [Fact]
    public void 点击按钮用绝对路径交给宿主打开()
    {
        using var temp = new TempDirectory();
        var artifact = Path.Combine(temp.Root, ".archify", "architecture-order", "order.html");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "<html></html>");

        var launcher = new RecordingFileLauncherService();
        Services.AddSingleton<IFileLauncherService>(launcher);

        var cut = RenderComponent<ArtifactLinks>(parameters => parameters
            .Add(component => component.Text, "已生成：`.archify/architecture-order/order.html`")
            .Add(component => component.WorkspaceRoot, temp.Root));

        cut.Find("button").Click();

        Assert.Equal(Path.GetFullPath(artifact), Assert.Single(launcher.Opened));
        cut.WaitForAssertion(() => Assert.Contains("已交给系统默认浏览器打开", cut.Markup));
    }

    [Fact]
    public void 文件不存在时按钮给可恢复提示且不启动进程()
    {
        using var temp = new TempDirectory();
        var launcher = new RecordingFileLauncherService();
        Services.AddSingleton<IFileLauncherService>(launcher);

        var cut = RenderComponent<ArtifactLinks>(parameters => parameters
            .Add(component => component.Text, "`.archify/a/missing.html`")
            .Add(component => component.WorkspaceRoot, temp.Root));

        cut.Find("button").Click();

        Assert.Empty(launcher.Opened);
        cut.WaitForAssertion(() => Assert.Contains("产物不存在", cut.Markup));
    }
}
