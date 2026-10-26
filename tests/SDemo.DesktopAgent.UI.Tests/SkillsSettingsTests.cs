using System.IO;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Settings;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>技能设置分区：目录包 / 单文件、版本、来源与哈希都能在界面上看到。</summary>
public sealed class SkillsSettingsTests : TestContext
{
    [Fact]
    public void 列出目录包与单文件并展示版本来源与哈希()
    {
        using var temp = new TempDirectory();
        var package = Path.Combine(temp.Root, "archify");
        Directory.CreateDirectory(Path.Combine(package, "references"));
        File.WriteAllText(
            Path.Combine(package, "SKILL.md"),
            "---\ndescription: 画架构图。\n---\n\n包正文");
        File.WriteAllText(
            Path.Combine(package, "skill-release.json"),
            """{"version":"3.2.0","source":{"repository":"https://github.com/tt-a1i/archify"}}""");
        File.WriteAllText(Path.Combine(package, "references", "delivery.md"), "交付契约");
        File.WriteAllText(Path.Combine(temp.Root, "summary.md"), "---\ndescription: 总结。\n---\n正文");

        Services.AddSingleton(new SkillCatalog(temp.Root));
        Services.AddSingleton<IFileLauncherService, NullFileLauncherService>();

        var cut = RenderComponent<SkillsSettings>();

        Assert.Contains("archify", cut.Markup);
        Assert.Contains("目录包", cut.Markup);
        Assert.Contains("v3.2.0", cut.Markup);
        Assert.Contains("https://github.com/tt-a1i/archify", cut.Markup);
        Assert.Contains("summary", cut.Markup);
        Assert.Contains("单文件", cut.Markup);
        Assert.Contains("2 个技能（1 个目录包）", cut.Markup);

        // 目录哈希在后台算，完成后才出现在界面上。
        cut.WaitForAssertion(() => Assert.Contains("sha256", cut.Markup));
    }
}
