using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 内置技能 <c>plugin-authoring</c>：随应用发布、启动时补进技能库。
/// 这些用例同时守住「技能包内容与仓库实际规则一致」：模板必须能通过插件校验，
/// SKILL.md 里引用的参考文件必须真实存在且可读。
/// </summary>
public sealed class BundledSkillTests : IDisposable
{
    private const string SkillName = BundledSkillInstaller.SkillName;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sdemo-bundled-skill-tests",
        Guid.NewGuid().ToString("N"));

    public BundledSkillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响断言。
        }
    }

    [Fact]
    public void 首次启动安装内置技能且技能库能发现它()
    {
        var result = new BundledSkillInstaller(_root).EnsureInstalled();

        Assert.True(result.Installed, result.Message);

        var catalog = new SkillCatalog(_root);
        var skill = Assert.Single(catalog.List(), item => item.Name == SkillName);

        Assert.Equal(SkillKind.Package, skill.Kind);
        Assert.Equal(BundledSkillInstaller.BundledVersion, skill.Version);
        Assert.False(skill.DisableModelInvocation);
        Assert.Contains("插件", skill.Description, StringComparison.Ordinal);
        Assert.Contains("插件", skill.WhenToUse, StringComparison.Ordinal);
        Assert.Contains("插件", skill.TagList);

        // 模型看到的是索引元数据；没有正文依赖。
        var entry = Assert.Single(catalog.Index(), item => item.Name == SkillName);
        Assert.Equal(skill.Description, entry.Description);
        Assert.False(entry.DisableModelInvocation);

        // 版本只有一个来源：安装器常量必须与技能自带的 release 清单一致。
        using var release = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_root, SkillName, "skill-release.json")));
        Assert.Equal(
            BundledSkillInstaller.BundledVersion,
            release.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void 技能包里引用的参考与模板都存在且可读()
    {
        Assert.True(new BundledSkillInstaller(_root).EnsureInstalled().Installed);

        var catalog = new SkillCatalog(_root);

        foreach (var path in new[]
                 {
                     "references/declarative.md",
                     "references/skill-file.md",
                     "references/validation.md",
                     "references/examples.md",
                     "references/builtin.md",
                     "templates/minimal/plugin.json",
                     "templates/minimal/skills/main.md",
                     "templates/full/plugin.json",
                     "templates/full/skills/main.md"
                 })
        {
            var file = catalog.ReadFile(SkillName, path);

            Assert.True(file.IsOk, $"{path}: {file.Status}");
            Assert.False(string.IsNullOrWhiteSpace(file.Content), path);
        }

        var skillRoot = Path.Combine(_root, SkillName);
        Assert.True(File.Exists(Path.Combine(skillRoot, "skill-release.json")));
        Assert.True(File.Exists(Path.Combine(skillRoot, BundledSkillInstaller.MarkerFileName)));
    }

    [Fact]
    public void 重复调用保持幂等且不覆盖已安装内容()
    {
        var installer = new BundledSkillInstaller(_root);
        Assert.True(installer.EnsureInstalled().Installed);

        var skillPath = Path.Combine(_root, SkillName, "SKILL.md");
        File.AppendAllText(skillPath, "\n\n<!-- 本地批注 -->\n");

        var second = installer.EnsureInstalled();

        Assert.False(second.Installed);
        Assert.Contains("本地批注", File.ReadAllText(skillPath), StringComparison.Ordinal);
        Assert.True(installer.IsInstalled);
    }

    [Fact]
    public void 发布版本变化时重装缺失文件()
    {
        var installer = new BundledSkillInstaller(_root);
        Assert.True(installer.EnsureInstalled().Installed);

        // 模拟上一版安装：把标记改成旧版本，并删掉一个文件。
        var markerPath = Path.Combine(_root, SkillName, BundledSkillInstaller.MarkerFileName);
        File.WriteAllText(markerPath, """{"skill":"plugin-authoring","version":"0.0.1"}""");

        var reference = Path.Combine(_root, SkillName, "references", "validation.md");
        File.Delete(reference);

        var result = installer.EnsureInstalled();

        Assert.True(result.Installed, result.Message);
        Assert.True(File.Exists(reference));

        using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
        Assert.Equal(
            BundledSkillInstaller.BundledVersion,
            document.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void 用户手写的同名技能不被内置安装覆盖()
    {
        var target = Path.Combine(_root, SkillName);
        Directory.CreateDirectory(target);

        var skillPath = Path.Combine(target, "SKILL.md");
        File.WriteAllText(skillPath, """
            ---
            description: 我自己写的插件制作笔记。
            ---

            正文
            """);

        var result = new BundledSkillInstaller(_root).EnsureInstalled();

        Assert.False(result.Installed);
        Assert.Contains("我自己写的", File.ReadAllText(skillPath), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(target, BundledSkillInstaller.MarkerFileName)));
    }

    [Theory]
    [InlineData("minimal", "my-plugin", 1)]
    [InlineData("full", "tone", 4)]
    public void 模板插件能通过插件校验并渲染提示词(string template, string pluginId, int mountCount)
    {
        Assert.True(new BundledSkillInstaller(_root).EnsureInstalled().Installed);

        var pluginsRoot = Path.Combine(_root, "plugins");
        CopyDirectory(
            Path.Combine(_root, SkillName, "templates", template),
            Path.Combine(pluginsRoot, pluginId));

        var load = new PluginCatalog(Array.Empty<IPluginPackage>(), pluginsRoot).Scan();

        Assert.Empty(load.Failures);

        var plugin = Assert.Single(load.Usable, item => item.Id == pluginId);
        Assert.False(plugin.HasErrors);
        var capability = Assert.Single(plugin.Capabilities);
        Assert.True(capability.IsUsable);
        Assert.Equal(mountCount, plugin.Mounts.Count);
        Assert.All(plugin.Mounts, mount => Assert.True(mount.IsUsable));

        var values = capability.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Default ?? parameter.Options.FirstOrDefault()?.Value ?? string.Empty,
            StringComparer.Ordinal);

        var prompt = new PluginPromptComposer().BuildSystemPrompt(capability, values);

        Assert.DoesNotContain("{{", prompt);
        Assert.DoesNotContain("---", prompt);
    }

    [Fact]
    public void 参考示例的清单与技能都能通过插件校验()
    {
        Assert.True(new BundledSkillInstaller(_root).EnsureInstalled().Installed);

        var examples = File.ReadAllText(
            Path.Combine(_root, SkillName, "references", "examples.md"));

        var blocks = ReadExampleBlocks(examples);

        Assert.Equal(4, blocks.Count);

        for (var index = 0; index < blocks.Count; index++)
        {
            var (json, markdown) = blocks[index];

            using var document = JsonDocument.Parse(json);
            var id = document.RootElement.GetProperty("id").GetString();
            var skill = document.RootElement
                .GetProperty("capabilities")[0]
                .GetProperty("skill")
                .GetString();

            var pluginsRoot = Path.Combine(_root, "example-plugins", index.ToString());
            var directory = Path.Combine(pluginsRoot, id!);
            var skillPath = Path.Combine(directory, skill!);
            Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), json);
            File.WriteAllText(skillPath, markdown);

            var load = new PluginCatalog(Array.Empty<IPluginPackage>(), pluginsRoot).Scan();

            Assert.Empty(load.Failures);
            var plugin = Assert.Single(load.Usable, item => item.Id == id);
            Assert.False(plugin.HasErrors);
            Assert.All(plugin.Mounts, mount => Assert.True(mount.IsUsable, mount.Key));
        }
    }

    /// <summary>从 references/examples.md 里按顺序取「```json 清单 + 紧跟的 ```markdown 技能」。</summary>
    private static IReadOnlyList<(string Json, string Markdown)> ReadExampleBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<(string Json, string Markdown)>();

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Trim().Equals("```json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var json = ReadFence(lines, ref index);

            while (++index < lines.Length
                   && !lines[index].Trim().Equals("```markdown", StringComparison.OrdinalIgnoreCase))
            {
            }

            if (index >= lines.Length)
            {
                break;
            }

            var markdown = ReadFence(lines, ref index);
            blocks.Add((json, markdown));
        }

        return blocks;
    }

    private static string ReadFence(string[] lines, ref int index)
    {
        var builder = new System.Text.StringBuilder();
        index++;

        while (index < lines.Length && !lines[index].Trim().Equals("```", StringComparison.Ordinal))
        {
            builder.AppendLine(lines[index]);
            index++;
        }

        return builder.ToString().TrimEnd();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
