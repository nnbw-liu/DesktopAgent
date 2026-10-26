using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SDemo.DesktopAgent.Services;

/// <summary>技能形态：旧式单文件，或包含 <c>SKILL.md</c> 与引用文件的目录包。</summary>
public enum SkillKind
{
    /// <summary><c>skills/name.md</c>：只有正文，没有包内引用。</summary>
    Flat,

    /// <summary><c>skills/name/SKILL.md</c>：Agent Skills 目录包，可带 references / scripts / assets。</summary>
    Package
}

/// <summary>技能库里的一个技能（包名即目录名，单文件名即技能名）。</summary>
/// <param name="Name">技能名。</param>
/// <param name="Description">一句话说明（悬浮提示与索引共用）。</param>
/// <param name="Icon">frontmatter 里的 <c>icon</c>；没写时为空，由 <see cref="Glyph"/> 取名字首字兜底。</param>
/// <param name="WhenToUse">索引里的「何时用」；没有时退回 description。</param>
/// <param name="Tags">路由用的标签（frontmatter 的 <c>tags</c>，逗号分隔）。</param>
/// <param name="DisableModelInvocation">frontmatter 的 disable-model-invocation：模型看不到、load_skill 也拒绝。</param>
/// <param name="Kind">单文件 / 目录包。</param>
/// <param name="RootPath">包根目录；单文件技能为技能库根目录。</param>
/// <param name="Version">包版本：优先 <c>skill-release.json</c>，其次 frontmatter。</param>
/// <param name="Source">包来源（<c>skill-release.json</c> 的 source.repository）；没有时为空。</param>
public sealed record SkillInfo(
    string Name,
    string Description,
    string Icon = "",
    string WhenToUse = "",
    IReadOnlyList<string>? Tags = null,
    bool DisableModelInvocation = false,
    SkillKind Kind = SkillKind.Flat,
    string RootPath = "",
    string Version = "",
    string Source = "")
{
    /// <summary>输入框 chip 里显示的字形：优先用 authored 的 <c>icon</c>，缺省取名字首字。</summary>
    public string Glyph => GlyphOf(!string.IsNullOrWhiteSpace(Icon) ? Icon : Name);

    /// <summary>非空标签列表（没有 frontmatter 时为空）。</summary>
    public IReadOnlyList<string> TagList => Tags ?? [];

    /// <summary>取一段文本的第一个字素：英文转大写，emoji 不会被拆成半个代理对。</summary>
    public static string GlyphOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "?";
        }

        var trimmed = text.Trim();
        var glyph = StringInfo.GetNextTextElement(trimmed);
        return glyph.ToUpperInvariant();
    }
}

/// <summary>技能包快照：文件数、总字节、目录内容哈希与最后写入时间（设置页展示用）。</summary>
public sealed record SkillSnapshot(
    int FileCount,
    long Bytes,
    string Hash,
    DateTime LastWriteUtc);

/// <summary>
/// 本地技能库：读取 %APPDATA%\SDemo\DesktopAgent\skills\。
/// <para>
/// 两种形态并存：<c>skills/name.md</c>（旧式单文件）与 <c>skills/name/SKILL.md</c>
/// （Agent Skills 目录包）。同名时目录包优先，旧文件仍保留但不进索引。
/// 包内引用通过 <c>read_skill_file</c> 按需读取，路径 jail 在本类完成。
/// </para>
/// M2 起系统提示只注入索引（<see cref="Index"/>），正文由 <c>load_skill</c> 按需读取；
/// 用户手选技能仍由聊天服务注入全文。
/// </summary>
public sealed class SkillCatalog : ISkillLibrary
{
    private const string WhenToUseKey = "when-to-use:";
    private const string SkillManifest = "SKILL.md";
    private const string ReleaseManifest = "skill-release.json";

    /// <summary>单次 <c>read_skill_file</c> 允许读入的最大文件，避免把大资产塞进内存。</summary>
    public const long MaxReferenceBytes = 1_000_000;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".json", ".txt" };

    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

    private readonly string _root;

    public SkillCatalog(IAppPaths paths)
        : this(paths.SkillsRoot)
    {
    }

    public SkillCatalog(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public IReadOnlyList<SkillInfo> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return Discover()
            .Select(ToInfo)
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>M2：只读 frontmatter 的索引元数据，不读正文。</summary>
    public IReadOnlyList<SkillIndexEntry> Index() =>
        List()
            .Select(skill => new SkillIndexEntry(
                skill.Name,
                skill.Description,
                string.IsNullOrWhiteSpace(skill.WhenToUse) ? skill.Description : skill.WhenToUse,
                skill.TagList,
                skill.DisableModelInvocation))
            .ToArray();

    /// <summary>读取技能全文；名字不存在时返回 null。包技能读 <c>SKILL.md</c>。</summary>
    public string? ReadContent(string? name)
    {
        var skill = Find(name);

        if (skill is null)
        {
            return null;
        }

        try
        {
            return File.ReadAllText(skill.EntryFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读取包内引用。只允许 .md / .json / .txt，路径必须落在包根目录内，
    /// 且不经过重解析点（符号链接 / junction）。
    /// </summary>
    public SkillFileReadResult ReadFile(string? name, string? path)
    {
        var skill = Find(name);

        if (skill is null)
        {
            return SkillFileReadResult.Fail(SkillFileStatus.SkillNotFound);
        }

        if (!skill.IsPackage)
        {
            return SkillFileReadResult.Fail(SkillFileStatus.FlatSkill);
        }

        var normalized = NormalizeRelativePath(path);

        if (normalized is null)
        {
            return SkillFileReadResult.Fail(SkillFileStatus.InvalidPath);
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(normalized)))
        {
            return SkillFileReadResult.Fail(SkillFileStatus.UnsupportedExtension, normalized);
        }

        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(skill.Root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return SkillFileReadResult.Fail(SkillFileStatus.InvalidPath, normalized);
        }

        var rootPrefix = Path.TrimEndingDirectorySeparator(skill.Root) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootPrefix, PathComparison))
        {
            return SkillFileReadResult.Fail(SkillFileStatus.InvalidPath, normalized);
        }

        try
        {
            // 逐段检查，禁止通过包内符号链接跳出包目录。
            var current = Path.TrimEndingDirectorySeparator(skill.Root);

            foreach (var segment in normalized.Split('/'))
            {
                current = Path.Combine(current, segment);

                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    return SkillFileReadResult.Fail(SkillFileStatus.FileNotFound, normalized);
                }

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return SkillFileReadResult.Fail(SkillFileStatus.InvalidPath, normalized);
                }
            }

            var info = new FileInfo(current);

            if (!info.Exists)
            {
                return SkillFileReadResult.Fail(SkillFileStatus.FileNotFound, normalized);
            }

            if (info.Length > MaxReferenceBytes)
            {
                return SkillFileReadResult.Fail(SkillFileStatus.TooLarge, normalized);
            }

            return SkillFileReadResult.Ok(File.ReadAllText(current), normalized, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SkillFileReadResult.Fail(SkillFileStatus.FileNotFound, normalized);
        }
    }

    /// <summary>计算包的目录内容哈希（SHA-256）；不存在时返回 null。设置页按需调用。</summary>
    public SkillSnapshot? Snapshot(string? name)
    {
        var skill = Find(name);

        if (skill is null)
        {
            return null;
        }

        try
        {
            if (!skill.IsPackage)
            {
                var info = new FileInfo(skill.EntryFile);

                return info.Exists
                    ? new SkillSnapshot(1, info.Length, Sha256File(info.FullName), info.LastWriteTimeUtc)
                    : null;
            }

            var files = Directory.EnumerateFiles(skill.Root, "*", SearchOption.AllDirectories)
                .Select(file => new FileInfo(file))
                .Where(file => file.Exists)
                .OrderBy(file => Path.GetRelativePath(skill.Root, file.FullName), StringComparer.Ordinal)
                .ToArray();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            var latest = DateTime.MinValue;

            foreach (var file in files)
            {
                bytes += file.Length;
                latest = file.LastWriteTimeUtc > latest ? file.LastWriteTimeUtc : latest;

                var relative = Path.GetRelativePath(skill.Root, file.FullName).Replace('\\', '/');
                hash.AppendData(Encoding.UTF8.GetBytes($"{relative}\n{file.Length}\n"));

                using var stream = File.OpenRead(file.FullName);
                var buffer = new byte[81920];
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }

            return new SkillSnapshot(
                files.Length,
                bytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 设置页用的一行短说明：版本、形态、文件数、总字节。
    /// 不在这里算哈希——大包在 UI 线程上读全文会卡住设置页；哈希由 <see cref="Snapshot"/> 异步算。
    /// </summary>
    public string? Summary(string? name)
    {
        var skill = Find(name);

        if (skill is null)
        {
            return null;
        }

        var (files, bytes) = Measure(skill);
        var version = ToInfo(skill).Version;
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(version))
        {
            parts.Add("v" + version);
        }

        parts.Add(skill.IsPackage ? "目录包" : "单文件");

        if (files > 0)
        {
            parts.Add($"{files} 个文件");
            parts.Add(FormatBytes(bytes));
        }

        return string.Join(" · ", parts);
    }

    private static (int Files, long Bytes) Measure(SkillDescriptor skill)
    {
        if (!skill.IsPackage)
        {
            var info = new FileInfo(skill.EntryFile);
            return info.Exists ? (1, info.Length) : (0, 0);
        }

        try
        {
            var count = 0;
            long bytes = 0;

            foreach (var file in Directory.EnumerateFiles(skill.Root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);

                if (info.Exists)
                {
                    count++;
                    bytes += info.Length;
                }
            }

            return (count, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------ 发现

    private sealed record SkillDescriptor(
        string Name,
        string EntryFile,
        string Root,
        bool IsPackage,
        string? ReleaseFile);

    private IReadOnlyList<SkillDescriptor> Discover()
    {
        var packages = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(directory);

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var manifest = Path.Combine(directory, SkillManifest);

            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                // 清单本身也不能是符号链接：否则包目录在库内，正文却在库外。
                if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var release = Path.Combine(directory, ReleaseManifest);
            packages[name] = new SkillDescriptor(
                name,
                manifest,
                directory,
                IsPackage: true,
                File.Exists(release) ? release : null);
        }

        var flats = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(_root, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            // 技能库根目录的 SKILL.md 不是技能本体。
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, "SKILL", StringComparison.OrdinalIgnoreCase)
                || packages.ContainsKey(name))
            {
                continue;
            }

            try
            {
                // 单文件技能同样拒绝符号链接，避免读到技能库外的内容。
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            flats[name] = new SkillDescriptor(name, file, _root, IsPackage: false, ReleaseFile: null);
        }

        // 目录包优先：同名扁平文件不覆盖包。
        foreach (var (name, descriptor) in flats)
        {
            packages.TryAdd(name, descriptor);
        }

        return packages.Values.ToArray();
    }

    private SkillDescriptor? Find(string? name)
    {
        var normalized = NormalizeName(name);

        if (normalized is null)
        {
            return null;
        }

        return Discover().FirstOrDefault(skill =>
            string.Equals(skill.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // 兼容旧调用「name.md」；同时挡掉目录分隔符。
        // 注意不能用 GetFileNameWithoutExtension：包名本身可以带点（如 my.skill）。
        var file = Path.GetFileName(name.Trim());

        if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^3];
        }

        return string.IsNullOrWhiteSpace(file) ? null : file;
    }

    private static SkillInfo ToInfo(SkillDescriptor skill)
    {
        var metadata = ReadMetadata(skill.EntryFile);
        var release = ReadRelease(skill.ReleaseFile);

        return new SkillInfo(
            skill.Name,
            metadata.Description,
            metadata.Icon,
            metadata.WhenToUse,
            metadata.Tags,
            metadata.DisableModelInvocation,
            skill.IsPackage ? SkillKind.Package : SkillKind.Flat,
            skill.Root,
            string.IsNullOrWhiteSpace(release.Version) ? metadata.Version : release.Version,
            string.IsNullOrWhiteSpace(release.Source) ? metadata.Source : release.Source);
    }

    private sealed record ReleaseInfo(string Version, string Source);

    private static ReleaseInfo ReadRelease(string? file)
    {
        if (file is null)
        {
            return new ReleaseInfo(string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;

            var version = root.TryGetProperty("version", out var versionValue)
                ? versionValue.GetString() ?? string.Empty
                : string.Empty;

            var source = root.TryGetProperty("source", out var sourceValue)
                          && sourceValue.TryGetProperty("repository", out var repository)
                ? repository.GetString() ?? string.Empty
                : string.Empty;

            return new ReleaseInfo(version, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseInfo(string.Empty, string.Empty);
        }
    }

    // ------------------------------------------------------------------ 元数据

    private sealed record Metadata(
        string Description,
        string Icon,
        string WhenToUse,
        IReadOnlyList<string> Tags,
        bool DisableModelInvocation,
        string Version,
        string Source);

    /// <summary>
    /// 技能摘要 + 图标 + 路由元数据：优先取 frontmatter；没有 frontmatter 时
    /// 摘要退回“第一行非空且不是标题”的文本，图标由调用方用名字兜底。
    /// </summary>
    private static Metadata ReadMetadata(string file)
    {
        var lines = File.ReadLines(file)
            .Take(80)
            .Select(line => line.Trim())
            .ToList();

        if (lines.Count > 0 && lines[0] == "---")
        {
            var end = lines.FindIndex(1, line => line == "---");

            if (end > 0)
            {
                var description = string.Empty;
                var icon = string.Empty;
                var whenToUse = string.Empty;
                var version = string.Empty;
                var source = string.Empty;
                var disableModelInvocation = false;
                IReadOnlyList<string> tags = [];

                for (var i = 1; i < end; i++)
                {
                    var line = lines[i];

                    if (description.Length == 0 && line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    {
                        description = Unquote(line["description:".Length..]);
                    }
                    else if (icon.Length == 0 && line.StartsWith("icon:", StringComparison.OrdinalIgnoreCase))
                    {
                        icon = Unquote(line["icon:".Length..]);
                    }
                    else if (whenToUse.Length == 0
                             && (line.StartsWith(WhenToUseKey, StringComparison.OrdinalIgnoreCase)
                                 || line.StartsWith("when_to_use:", StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = line.StartsWith(WhenToUseKey, StringComparison.OrdinalIgnoreCase)
                            ? line[WhenToUseKey.Length..]
                            : line["when_to_use:".Length..];
                        whenToUse = Unquote(value);
                    }
                    else if (tags.Count == 0 && line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        tags = ParseTags(line["tags:".Length..]);
                    }
                    else if (version.Length == 0 && line.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                    {
                        version = Unquote(line["version:".Length..]);
                    }
                    else if (source.Length == 0 && line.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
                    {
                        source = Unquote(line["source:".Length..]);
                    }
                    else if (IsDisableModelInvocationLine(line))
                    {
                        disableModelInvocation = true;
                    }
                }

                if (description.Length == 0)
                {
                    description = lines.Skip(end + 1)
                        .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
                }

                return new Metadata(description, icon, whenToUse, tags, disableModelInvocation, version, source);
            }
        }

        var summary = lines.FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;
        return new Metadata(summary, string.Empty, string.Empty, [], false, string.Empty, string.Empty);
    }

    /// <summary>支持 <c>disable-model-invocation: true</c> 与裸键 <c>disable-model-invocation</c> 两种写法。</summary>
    internal static bool IsDisableModelInvocationLine(string line)
    {
        const string key = "disable-model-invocation";
        var trimmed = line.Trim();

        if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[key.Length..].TrimStart();

        if (rest.Length == 0)
        {
            return true;
        }

        if (rest[0] != ':')
        {
            return false;
        }

        return !rest[1..].Trim().StartsWith("false", StringComparison.OrdinalIgnoreCase)
            && !rest[1..].Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>支持 <c>[a, b]</c> 与 <c>a, b</c> 两种写法；空值返回空列表。</summary>
    internal static IReadOnlyList<string> ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Unquote(string value) => value.Trim().Trim('"', '\'', '“', '”');

    // ------------------------------------------------------------------ 路径

    /// <summary>规范化包内相对路径；不合法时返回 null。</summary>
    private static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim().Replace('\\', '/');

        if (value.StartsWith('/')
            || value.Contains(':')
            || value.Any(char.IsControl)
            || Path.IsPathRooted(value))
        {
            return null;
        }

        var segments = value.Split('/');

        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || ReservedNames.Contains(Path.GetFileNameWithoutExtension(segment)))
            {
                return null;
            }
        }

        return string.Join('/', segments);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };
}
