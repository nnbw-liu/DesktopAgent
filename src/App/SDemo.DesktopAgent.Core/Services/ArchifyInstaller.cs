using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// P3：把上游 Archify 3.2.0 安装到 <c>&lt;DataRoot&gt;/skills/archify</c>。
/// 复制时剔除 <c>examples/*.html</c>（约 12 MB），把上游 <c>SKILL.md</c> 移到
/// <c>references/upstream-SKILL.md</c>，再写入 DesktopAgent 适配版入口；
/// 许可 / NOTICE / 第三方声明一并保留。
/// </summary>
public sealed class ArchifyInstaller : IArchifyInstaller
{
    public const string SkillName = "archify";

    public const string SupportedVersion = "3.2.0";

    public const string AdapterVersion = "1";

    private const string AdapterResource = "SDemo.DesktopAgent.Assets.archify-desktop-SKILL.md";
    private const string NoticesResource = "SDemo.DesktopAgent.Assets.archify-THIRD_PARTY_NOTICES.md";

    private readonly SkillCatalog _catalog;

    public ArchifyInstaller(SkillCatalog catalog)
    {
        _catalog = catalog;
    }

    public string TargetRoot => Path.Combine(_catalog.Root, SkillName);

    public bool IsInstalled => File.Exists(Path.Combine(TargetRoot, "SKILL.md"));

    public async Task<ArchifyInstallResult> InstallAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return ArchifyInstallResult.Fail("请选择一个包含 archify/ 或 bin/archify.mjs 的上游目录。");
        }

        string source;

        try
        {
            source = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return ArchifyInstallResult.Fail("安装源路径不合法。");
        }

        if (!Directory.Exists(source))
        {
            return ArchifyInstallResult.Fail($"安装源不存在：{source}");
        }

        var (packageRoot, repositoryRoot) = ResolveLayout(source);

        if (packageRoot is null)
        {
            return ArchifyInstallResult.Fail(
                "没有找到 Archify 技能包：请选择仓库根（含 archify/）或技能包目录（含 SKILL.md 与 bin/archify.mjs）。");
        }

        if (!File.Exists(Path.Combine(packageRoot, "SKILL.md"))
            || !File.Exists(Path.Combine(packageRoot, "bin", "archify.mjs")))
        {
            return ArchifyInstallResult.Fail("技能包缺少 SKILL.md 或 bin/archify.mjs。");
        }

        var release = ReadRelease(packageRoot);
        var version = release?.Version;

        if (string.IsNullOrWhiteSpace(version))
        {
            return ArchifyInstallResult.Fail("技能包缺少 skill-release.json，无法确认版本。");
        }

        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            return ArchifyInstallResult.Fail(
                $"当前只支持 Archify {SupportedVersion}，选择的包是 {version}。");
        }

        // 防止把已经适配过的包再当上游安装：根 SKILL.md 会被当上游正文移走，导致链路失真。
        if (File.ReadAllText(Path.Combine(packageRoot, "SKILL.md"))
            .Contains("DesktopAgent 适配", StringComparison.Ordinal))
        {
            return ArchifyInstallResult.Fail(
                "这个目录已经是 DesktopAgent 适配包，请选择上游 archify 目录重新安装。");
        }

        var target = TargetRoot;

        if (Directory.Exists(target))
        {
            try
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                {
                    return ArchifyInstallResult.Fail("技能库里的 archify 是符号链接，拒绝覆盖。");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ArchifyInstallResult.Fail($"无法检查已有技能目录：{ex.Message}");
            }
        }

        var stage = Path.Combine(Path.GetTempPath(), $"sdemo-archify-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(stage);
            CopyPackage(packageRoot, stage, cancellationToken);

            var upstream = Path.Combine(stage, "references", "upstream-SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(upstream)!);
            File.Move(Path.Combine(stage, "SKILL.md"), upstream, overwrite: true);
            WriteAdapter(stage);

            CopyNotices(repositoryRoot, stage);
            EnsureLicense(stage);

            var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).ToArray();
            var bytes = files.Sum(file => new FileInfo(file).Length);

            WriteManifest(stage, repositoryRoot, version, files.Length, bytes);

            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(_catalog.Root);
            MoveOrCopy(stage, target);

            return new ArchifyInstallResult(
                true,
                $"已安装 Archify {version}：{files.Length} 个文件，{FormatBytes(bytes)}。",
                target,
                version,
                release?.Repository ?? repositoryRoot,
                files.Length,
                bytes);
        }
        catch (OperationCanceledException)
        {
            return ArchifyInstallResult.Fail("安装已取消。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ArchifyInstallResult.Fail($"安装失败：{ex.Message}");
        }
        finally
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

    private static (string? PackageRoot, string RepositoryRoot) ResolveLayout(string source)
    {
        if (File.Exists(Path.Combine(source, "SKILL.md"))
            && File.Exists(Path.Combine(source, "bin", "archify.mjs")))
        {
            return (source, Directory.GetParent(source)?.FullName ?? source);
        }

        var package = Path.Combine(source, "archify");

        if (File.Exists(Path.Combine(package, "SKILL.md"))
            && File.Exists(Path.Combine(package, "bin", "archify.mjs")))
        {
            return (package, source);
        }

        return (null, source);
    }

    private static ReleaseInfo? ReadRelease(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "skill-release.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;
        var repository = root.TryGetProperty("source", out var sourceElement)
                         && sourceElement.TryGetProperty("repository", out var repositoryElement)
            ? repositoryElement.GetString()
            : null;

        return new ReleaseInfo(version, repository);
    }

    /// <summary>复制包内容；示例 HTML 只用于上游演示，剔除后体积从 17 MB 降到约 5 MB。</summary>
    private static void CopyPackage(string packageRoot, string stage, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));

        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                     IgnoreInaccessible = false
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.StartsWith("examples/", StringComparison.OrdinalIgnoreCase)
                && relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void WriteAdapter(string stage)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(AdapterResource)
            ?? throw new InvalidOperationException("缺少适配技能资源。");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        File.WriteAllText(Path.Combine(stage, "SKILL.md"), reader.ReadToEnd(), new UTF8Encoding(false));
    }

    private static void CopyNotices(string repositoryRoot, string stage)
    {
        var source = Path.Combine(repositoryRoot, "THIRD_PARTY_NOTICES.md");

        if (File.Exists(source))
        {
            File.Copy(source, Path.Combine(stage, "THIRD_PARTY_NOTICES.md"), overwrite: true);
            return;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(NoticesResource)
            ?? throw new InvalidOperationException("缺少第三方声明资源。");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(stage, "THIRD_PARTY_NOTICES.md"),
            reader.ReadToEnd(),
            new UTF8Encoding(false));
    }

    private static void EnsureLicense(string stage)
    {
        foreach (var name in new[] { "LICENSE", "NOTICE" })
        {
            if (!File.Exists(Path.Combine(stage, name)))
            {
                throw new IOException($"技能包缺少 {name}，无法确认许可。");
            }
        }
    }

    private static void WriteManifest(
        string stage,
        string repositoryRoot,
        string version,
        int files,
        long bytes)
    {
        var manifest = new
        {
            skill = SkillName,
            upstreamVersion = version,
            adapterVersion = AdapterVersion,
            source = repositoryRoot,
            installedAt = DateTimeOffset.Now.ToString("O"),
            files,
            bytes
        };

        File.WriteAllText(
            Path.Combine(stage, "desktop-install.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void MoveOrCopy(string stage, string target)
    {
        try
        {
            Directory.Move(stage, target);
        }
        catch (IOException)
        {
            // 临时目录与目标不在同一个卷时退回逐文件复制。
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

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    private sealed record ReleaseInfo(string? Version, string? Repository);
}
