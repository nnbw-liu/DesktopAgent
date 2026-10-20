using System.IO;
using SDemo.DesktopAgent.Plugins;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 临时插件目录：每个测试一份，互不影响；内置目录与本地目录分开建。
/// </summary>
internal sealed class TempPlugins : IDisposable
{
    public TempPlugins()
    {
        Root = Path.Combine(Path.GetTempPath(), "sdemo-plugin-tests", Guid.NewGuid().ToString("N"));
        BuiltinRoot = Path.Combine(Root, "builtin");
        LocalRoot = Path.Combine(Root, "local");
        Directory.CreateDirectory(BuiltinRoot);
        Directory.CreateDirectory(LocalRoot);
    }

    public string Root { get; }

    public string BuiltinRoot { get; }

    public string LocalRoot { get; }

    /// <summary>写一个插件目录，返回目录完整路径。</summary>
    public string WritePlugin(
        string root,
        string folder,
        string manifestJson,
        params (string RelativePath, string Content)[] files)
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifestJson);

        foreach (var (relativePath, content) in files)
        {
            var file = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(parent);
            File.WriteAllText(file, content);
        }

        return directory;
    }

    public string WriteBuiltin(string folder, string manifestJson, params (string, string)[] files) =>
        WritePlugin(BuiltinRoot, folder, manifestJson, files);

    public string WriteLocal(string folder, string manifestJson, params (string, string)[] files) =>
        WritePlugin(LocalRoot, folder, manifestJson, files);

    public PluginCatalog CreateCatalog(PluginHostVersion? hostVersion = null) =>
        hostVersion is null
            ? new PluginCatalog(BuiltinRoot, LocalRoot)
            : new PluginCatalog(BuiltinRoot, LocalRoot, hostVersion);

    public PluginLoadResult Scan(PluginHostVersion? hostVersion = null) =>
        CreateCatalog(hostVersion).Scan();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响测试结果。
        }
    }
}

/// <summary>测试用的清单 / 技能文本。</summary>
internal static class Fixtures
{
    public const string Skill = """
        ---
        name: 演示
        description: 一句话说明。
        version: 1.0
        ---

        你会收到一段文本，按规则处理。
        """;

    /// <summary>最小可用清单（一个能力 + 一个输入框入口）。</summary>
    public static string Minimal(string id = "demo", string name = "演示", string skill = "skills/main.md") => $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "capabilities": [
            { "id": "main", "name": "演示", "skill": "{{skill}}" }
          ],
          "mounts": [
            { "id": "composer", "surface": "composer", "capability": "main" }
          ]
        }
        """;
}
