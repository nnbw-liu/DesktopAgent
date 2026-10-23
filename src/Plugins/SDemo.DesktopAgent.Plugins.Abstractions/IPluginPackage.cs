using System.Reflection;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 插件内容的统一读取入口：目录插件从磁盘读，内置插件从程序集嵌入资源读。
/// 校验器只依赖这个接口，因此两种来源走完全相同的规则与错误信息。
/// </summary>
public interface IPluginContentSource
{
    /// <summary>展示 / 诊断用的根路径，例如 <c>C:\...\plugins\translate</c> 或 <c>builtin://translate</c>。</summary>
    string DisplayPath { get; }

    /// <summary>
    /// 按插件内的相对路径（<c>plugin.json</c> / <c>skills/translate.md</c>）读取文本。
    /// 读取失败时返回 false，并通过 <paramref name="error"/> 给出可直接展示的中文原因。
    /// </summary>
    bool TryRead(string relativePath, out string content, out long byteLength, out string? error);
}

/// <summary>
/// 一个插件包。内置插件把清单与技能作为嵌入资源打进自己的类库；
/// 本地插件由目录内容源在运行时提供。两者在校验器/注册表看来完全一致。
/// </summary>
public interface IPluginPackage
{
    /// <summary>稳定 id（与 plugin.json 的 id 一致，用于日志与去重）。</summary>
    string Id { get; }

    /// <summary>plugin.json 原文。</summary>
    string ManifestJson { get; }

    /// <summary>技能等文件的读取入口。</summary>
    IPluginContentSource Content { get; }
}

/// <summary>插件包内容的大小上限。具体校验仍在 <c>PluginValidator</c>，这里只防一次性读入超大文件。</summary>
public static class PluginContentLimits
{
    /// <summary>单个文件最多读取的字节数（4 MB）。</summary>
    public const long MaxFileBytes = 4 * 1024 * 1024;
}

/// <summary>
/// 程序集嵌入资源形式的内容源。资源逻辑名就是插件内相对路径
/// （构建时通过 <c>EmbeddedResource LogicalName</c> 固定为 <c>plugin.json</c> / <c>skills/x.md</c>）。
/// </summary>
public sealed class EmbeddedPluginContent : IPluginContentSource
{
    private readonly IReadOnlyDictionary<string, string> _files;

    private EmbeddedPluginContent(string displayPath, IReadOnlyDictionary<string, string> files)
    {
        DisplayPath = displayPath;
        _files = files;
    }

    public string DisplayPath { get; }

    /// <summary>读取程序集里的全部嵌入文本资源；<paramref name="displayPath"/> 用于设置页展示。</summary>
    public static EmbeddedPluginContent FromAssembly(Assembly assembly, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            // 只收文本资源（plugin.json / *.md），忽略程序集里可能出现的其它资源。
            var normalized = resourceName.Replace('\\', '/');
            if (!IsPluginTextResource(normalized))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            files[normalized] = reader.ReadToEnd();
        }

        return new EmbeddedPluginContent(displayPath, files);
    }

    public bool TryRead(string relativePath, out string content, out long byteLength, out string? error)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        if (_files.TryGetValue(normalized, out var text))
        {
            content = text;
            byteLength = System.Text.Encoding.UTF8.GetByteCount(text);
            error = null;
            return true;
        }

        content = string.Empty;
        byteLength = 0;
        error = $"内置插件缺少文件：{normalized}";
        return false;
    }

    private static bool IsPluginTextResource(string name) =>
        name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 内置插件包的基类：把「id + 程序集嵌入资源」的样板代码收敛到一处。
/// 新增内置插件＝新增一个类库，写一个三行的子类。
/// </summary>
public abstract class EmbeddedPluginPackage : IPluginPackage
{
    private readonly EmbeddedPluginContent _content;

    protected EmbeddedPluginPackage(string id, Assembly assembly)
    {
        Id = id;
        _content = EmbeddedPluginContent.FromAssembly(assembly, $"builtin://{id}");
    }

    public string Id { get; }

    public string ManifestJson =>
        _content.TryRead("plugin.json", out var json, out _, out var error)
            ? json
            : throw new InvalidOperationException($"内置插件 {Id} 缺少 plugin.json：{error}");

    public IPluginContentSource Content => _content;
}
