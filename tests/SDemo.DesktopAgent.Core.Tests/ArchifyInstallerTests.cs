using System.Text.Json;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P3：Archify 安装器只认 3.2.0，写适配入口、保留上游正文 / 许可，剔除示例 HTML。</summary>
public sealed class ArchifyInstallerTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _skills;
    private readonly string _source;

    public ArchifyInstallerTests()
    {
        _skills = Path.Combine(_root.Root, "skills");
        _source = Path.Combine(_root.Root, "repo");
        Directory.CreateDirectory(_skills);
        WritePackage(Path.Combine(_source, "archify"), "3.2.0");
        File.WriteAllText(Path.Combine(_source, "THIRD_PARTY_NOTICES.md"), "第三方声明");
    }

    [Fact]
    public async Task 安装后技能库出现适配包并保留上游正文与许可()
    {
        var installer = new ArchifyInstaller(new SkillCatalog(_skills));

        var result = await installer.InstallAsync(_source);

        Assert.True(result.Success, result.Message);
        Assert.True(installer.IsInstalled);
        Assert.Equal("3.2.0", result.Version);
        Assert.True(result.Files > 0);

        var target = Path.Combine(_skills, "archify");
        Assert.True(File.Exists(Path.Combine(target, "SKILL.md")));
        Assert.Contains("DesktopAgent 适配", File.ReadAllText(Path.Combine(target, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(target, "references", "upstream-SKILL.md")));
        Assert.Contains("上游正文", File.ReadAllText(Path.Combine(target, "references", "upstream-SKILL.md")));
        Assert.False(File.Exists(Path.Combine(target, "examples", "demo.html")));
        Assert.True(File.Exists(Path.Combine(target, "THIRD_PARTY_NOTICES.md")));
        Assert.Contains("第三方声明", File.ReadAllText(Path.Combine(target, "THIRD_PARTY_NOTICES.md")));
        Assert.True(File.Exists(Path.Combine(target, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(target, "NOTICE")));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "desktop-install.json")));
        Assert.Equal("3.2.0", manifest.RootElement.GetProperty("upstreamVersion").GetString());

        var skill = Assert.Single(new SkillCatalog(_skills).List());
        Assert.Equal("archify", skill.Name);
        Assert.Equal(SkillKind.Package, skill.Kind);
        Assert.Equal("3.2.0", skill.Version);
        Assert.Contains("DesktopAgent 适配", skill.Description);
    }

    [Fact]
    public async Task 直接选技能包目录也能安装()
    {
        var installer = new ArchifyInstaller(new SkillCatalog(_skills));

        var result = await installer.InstallAsync(Path.Combine(_source, "archify"));

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(_skills, "archify", "bin", "archify.mjs")));
    }

    [Fact]
    public async Task 重复安装覆盖旧包且不留下临时目录()
    {
        var installer = new ArchifyInstaller(new SkillCatalog(_skills));
        Assert.True((await installer.InstallAsync(_source)).Success);
        File.WriteAllText(Path.Combine(_skills, "archify", "stale.txt"), "旧文件");

        Assert.True((await installer.InstallAsync(_source)).Success);

        Assert.False(File.Exists(Path.Combine(_skills, "archify", "stale.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_skills),
            directory => Path.GetFileName(directory).StartsWith("sdemo-archify-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 已安装的适配包不能再当上游安装()
    {
        var installer = new ArchifyInstaller(new SkillCatalog(_skills));
        Assert.True((await installer.InstallAsync(_source)).Success);

        var result = await installer.InstallAsync(Path.Combine(_skills, "archify"));

        Assert.False(result.Success);
        Assert.Contains("已经是 DesktopAgent 适配包", result.Message);
    }

    [Fact]
    public async Task 非_3_2_0_版本被拒绝()
    {
        var other = Path.Combine(_root.Root, "other");
        WritePackage(Path.Combine(other, "archify"), "3.1.0");

        var result = await new ArchifyInstaller(new SkillCatalog(_skills)).InstallAsync(other);

        Assert.False(result.Success);
        Assert.Contains("3.2.0", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_skills, "archify")));
    }

    [Fact]
    public async Task 缺少技能包时给可恢复提示()
    {
        var empty = Path.Combine(_root.Root, "empty");
        Directory.CreateDirectory(empty);

        var result = await new ArchifyInstaller(new SkillCatalog(_skills)).InstallAsync(empty);

        Assert.False(result.Success);
        Assert.Contains("没有找到", result.Message);
    }

    private static void WritePackage(string root, string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "references"));
        Directory.CreateDirectory(Path.Combine(root, "examples"));

        File.WriteAllText(
            Path.Combine(root, "SKILL.md"),
            $"""
             ---
             name: archify
             description: 上游技能
             metadata:
               version: "3.2"
             ---

             上游正文
             """);
        File.WriteAllText(Path.Combine(root, "bin", "archify.mjs"), "console.log('archify');");
        File.WriteAllText(
            Path.Combine(root, "skill-release.json"),
            JsonSerializer.Serialize(new
            {
                version,
                source = new { repository = "https://github.com/tt-a1i/archify" }
            }));
        File.WriteAllText(Path.Combine(root, "references", "delivery-contract.md"), "交付契约");
        File.WriteAllText(Path.Combine(root, "examples", "demo.html"), "<html>示例</html>");
        File.WriteAllText(Path.Combine(root, "LICENSE"), "Apache License 2.0");
        File.WriteAllText(Path.Combine(root, "NOTICE"), "Archify NOTICE");
    }

    public void Dispose() => _root.Dispose();
}
