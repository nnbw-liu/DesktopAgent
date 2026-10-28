using System.IO;
using System.Text;
using System.Text.Json;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一次内置技能安装的结果。<see cref="Installed"/> 为 false 表示已存在且不需要动。
/// </summary>
public sealed record BundledSkillInstallResult(bool Installed, string Message);

/// <summary>
/// 把随应用发布的内置技能补进技能库（<c>&lt;SkillsRoot&gt;/&lt;skill&gt;/</c>）。
/// <para>
/// 源码里的技能包在 <c>Assets/skills/&lt;name&gt;/</c>，构建时按 <c>skills/&lt;name&gt;/**</c>
/// 作为嵌入资源打进 Core 程序集；启动时按同样的相对路径写到磁盘。
/// 所以用户既能在「设置 → 技能」里看到它、用 <c>/技能名</c> 调用它，也能用文本编辑器打开它。
/// </para>
/// <para>
/// 幂等且有版本闸：安装目录里的 <c>desktop-install.json</c> 记录版本，版本一致就跳过；
/// 技能库里已有同名但**不是**内置安装的目录时保持原样（用户手写的同名技能优先）。
/// </para>
/// </summary>
public sealed class BundledSkillInstaller
{
    /// <summary>内置技能名，等于技能库里的目录名。</summary>
    public const string SkillName = "plugin-authoring";

    /// <summary>内置技能版本；改内容时递增，下次启动会覆盖旧的内置安装。</summary>
    public const string BundledVersion = "1.0.0";

    /// <summary>安装标记文件名；没有它说明目录不是内置安装写的。</summary>
    public const string MarkerFileName = "desktop-install.json";

    private const string ResourcePrefix = "skills/" + SkillName + "/";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;

    public BundledSkillInstaller(SkillCatalog catalog)
        : this(catalog.Root)
    {
    }

    public BundledSkillInstaller(string skillsRoot)
    {
        _root = Path.GetFullPath(skillsRoot);
    }

    /// <summary>技能库根目录。</summary>
    public string Root => _root;

    /// <summary>内置技能在技能库里的目录。</summary>
    public string TargetRoot => Path.Combine(_root, SkillName);

    /// <summary>技能库里是否已经有 <c>&lt;name&gt;/SKILL.md</c>。</summary>
    public bool IsInstalled => File.Exists(Path.Combine(TargetRoot, "SKILL.md"));

    /// <summary>
    /// 确保技能库里有一份同版本的内置技能。启动时调用；任何失败都只返回结论，不抛到 UI。
    /// </summary>
    public BundledSkillInstallResult EnsureInstalled()
    {
        string? stage = null;

        try
        {
            var target = TargetRoot;

            if (Directory.Exists(target))
            {
                if (IsReparsePoint(target))
                {
                    return new(false, $"技能库里的 {SkillName} 是符号链接，拒绝覆盖：{target}");
                }

                var installed = ReadInstalledVersion(Path.Combine(target, MarkerFileName));

                if (installed is null)
                {
                    // 同名目录不是内置安装写的（用户自制 / 手工解压）：保持原样。
                    return new(false, $"技能库里已有同名目录，保持原样：{target}");
                }

                if (string.Equals(installed, BundledVersion, StringComparison.Ordinal))
                {
                    return new(false, $"内置技能 {SkillName} 已是最新（{BundledVersion}）。");
                }
            }

            var files = CollectResources();

            if (files.Count == 0)
            {
                return new(false, $"程序集里缺少内置技能资源（{ResourcePrefix}），未安装。");
            }

            stage = Path.Combine(Path.GetTempPath(), $"sdemo-skill-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);

            long bytes = 0;

            foreach (var (relative, content) in files)
            {
                var destination = Path.Combine(
                    stage,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, content, Utf8NoBom);
                bytes += Utf8NoBom.GetByteCount(content);
            }

            WriteMarker(stage, files.Count, bytes);

            Directory.CreateDirectory(_root);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            MoveOrCopy(stage, target);

            return new(true, $"已安装内置技能 {SkillName} {BundledVersion}（{files.Count} 个文件）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(false, $"内置技能安装失败：{ex.Message}");
        }
        finally
        {
            if (stage is not null)
            {
                try
                {
                    if (Directory.Exists(stage))
                    {
                        Directory.Delete(stage, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 临时目录清不掉不影响安装结果。
                }
            }
        }
    }

    /// <summary>读取程序集里的内置技能文本资源，返回「相对技能目录的路径 + 正文」。</summary>
    private static IReadOnlyList<(string Relative, string Content)> CollectResources()
    {
        var assembly = typeof(BundledSkillInstaller).Assembly;
        var files = new List<(string Relative, string Content)>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var normalized = resourceName.Replace('\\', '/');

            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = normalized[ResourcePrefix.Length..];

            if (relative.Length == 0)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            files.Add((relative, reader.ReadToEnd()));
        }

        return files
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>读安装标记里的版本；文件缺失或读坏都当作「不是内置安装」。</summary>
    private static string? ReadInstalledVersion(string markerPath)
    {
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));

            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteMarker(string stage, int files, long bytes)
    {
        var marker = new
        {
            skill = SkillName,
            version = BundledVersion,
            installer = "desktopagent-bundled-skill",
            installedAt = DateTimeOffset.Now.ToString("O"),
            files,
            bytes
        };

        File.WriteAllText(
            Path.Combine(stage, MarkerFileName),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }),
            Utf8NoBom);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到属性时按普通目录处理，交给后续写入报错。
            return false;
        }
    }

    private static void MoveOrCopy(string stage, string target)
    {
        try
        {
            Directory.Move(stage, target);
        }
        catch (IOException)
        {
            // 临时目录与技能库不在同一个卷时退回逐文件复制。
            CopyDirectory(stage, target);
            Directory.Delete(stage, recursive: true);
        }
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
