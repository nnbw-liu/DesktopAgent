using System.IO;
using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>校验结果：<see cref="Plugin"/> 为 null 表示整个插件不可用，原因见 <see cref="FatalIssues"/>。</summary>
public sealed record PluginValidationResult(PluginDescriptor? Plugin, IReadOnlyList<PluginIssue> FatalIssues)
{
    public bool Success => Plugin is not null;
}

/// <summary>skill 文件的 YAML frontmatter（只认 name / description / version 三个字段）。</summary>
internal sealed record SkillFrontmatter(string? Name, string? Description, string? Version)
{
    public static SkillFrontmatter Empty { get; } = new(null, null, null);
}

/// <summary>
/// 清单与技能文件的校验器：id 格式、skill 路径越界、占位符、参数 kind、minHostVersion、文件体积都在这里判。
/// 约定：<see cref="PluginIssueSeverity.Error"/> 让所在单元（插件 / capability / mount）不可用，
/// <see cref="PluginIssueSeverity.Warning"/> 只提示，不影响运行。
/// </summary>
public sealed class PluginValidator
{
    /// <summary>显示名上限。</summary>
    public const int MaxNameLength = 24;

    /// <summary>单个 skill 文件大小上限。</summary>
    public const long MaxSkillBytes = 256 * 1024;

    private static readonly string[] SupportedDefaultFrom = ["system-language", "system-locale"];

    private static readonly Regex IdPattern = new(
        "^[a-z][a-z0-9-]{1,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParameterKeyPattern = new(
        "^[a-z][a-zA-Z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberPattern = new(
        @"^-?\d+(\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 只放行 SVG path 数据需要的字符，杜绝整段 SVG / 事件属性被塞进来。
    private static readonly Regex IconPathPattern = new(
        @"^[MmLlHhVvCcSsQqTtAaZz0-9.,\-\s]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*([a-z][a-zA-Z0-9_]*)\s*(?:\.\s*label\s*)?\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RawPlaceholderPattern = new(
        @"\{\{[^{}]*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly PluginHostVersion _hostVersion;

    public PluginValidator()
        : this(PluginHostVersion.Current)
    {
    }

    public PluginValidator(PluginHostVersion hostVersion) => _hostVersion = hostVersion;

    /// <summary>
    /// 把清单转成领域模型，并把所有问题收集到结果里。
    /// 只由 <see cref="PluginCatalog"/> 调用；外部想按目录校验请直接用 Catalog（它会同时处理体积、编码与异常）。
    /// </summary>
    internal PluginValidationResult Build(PluginManifestDto manifest, string pluginRoot, PluginSource source)
    {
        var fatal = new List<PluginIssue>();
        var pluginIssues = new List<PluginIssue>();

        // schemaVersion：v1 不认识的更高版本直接拒绝，避免按错的规则解读。
        if (manifest.SchemaVersion is { } schemaVersion && schemaVersion > 1)
        {
            fatal.Add(Error($"清单 schemaVersion={schemaVersion}，本应用只支持 1", "schemaVersion"));
        }

        var id = manifest.Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            fatal.Add(Error("缺少 id", "id"));
        }
        else if (!IdPattern.IsMatch(id))
        {
            fatal.Add(Error("id 必须匹配 ^[a-z][a-z0-9-]{1,31}$（小写字母开头，只含小写字母、数字、连字符）", "id"));
        }

        var name = manifest.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            fatal.Add(Error("缺少 name", "name"));
        }
        else if (name.Length > MaxNameLength)
        {
            pluginIssues.Add(Warning($"name 超过 {MaxNameLength} 个字符，已截断", "name"));
            name = TruncateByTextElements(name, MaxNameLength);
        }

        if (fatal.Count > 0)
        {
            return new PluginValidationResult(null, fatal);
        }

        var capabilityDtos = (manifest.Capabilities ?? []).Where(item => item is not null).ToList();
        var mountDtos = (manifest.Mounts ?? []).Where(item => item is not null).ToList();

        if (capabilityDtos.Count == 0)
        {
            fatal.Add(Error("capabilities 至少要有一个能力", "capabilities"));
        }

        if (mountDtos.Count == 0)
        {
            fatal.Add(Error("mounts 至少要有一个入口", "mounts"));
        }

        var duplicateCapability = capabilityDtos
            .Select(item => item.Id?.Trim() ?? string.Empty)
            .Where(idValue => idValue.Length > 0)
            .GroupBy(idValue => idValue, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCapability is not null)
        {
            fatal.Add(Error($"capabilities 的 id 重复：\"{duplicateCapability.Key}\"", "capabilities"));
        }

        var duplicateMount = mountDtos
            .Select(item => item.Id?.Trim() ?? string.Empty)
            .Where(idValue => idValue.Length > 0)
            .GroupBy(idValue => idValue, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMount is not null)
        {
            fatal.Add(Error($"mounts 的 id 重复：\"{duplicateMount.Key}\"", "mounts"));
        }

        if (fatal.Count > 0)
        {
            return new PluginValidationResult(null, fatal);
        }

        // 插件级可选字段。
        var icon = BuildIcon(manifest.Icon, pluginIssues);

        var state = PluginState.Available;
        string? disabledReason = null;

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            if (!PluginHostVersion.TryParse(manifest.MinHostVersion, out var minimum))
            {
                pluginIssues.Add(Warning($"minHostVersion \"{manifest.MinHostVersion}\" 不是合法版本号，已忽略", "minHostVersion"));
            }
            else if (minimum.CompareTo(_hostVersion) > 0)
            {
                state = PluginState.Disabled;
                disabledReason = $"需要宿主 {minimum} 或更高版本（当前 {_hostVersion}）";
            }
        }

        var capabilities = new List<PluginCapability>(capabilityDtos.Count);
        for (var index = 0; index < capabilityDtos.Count; index++)
        {
            capabilities.Add(BuildCapability(capabilityDtos[index]!, index, pluginRoot));
        }

        var mounts = new List<PluginMount>(mountDtos.Count);
        for (var index = 0; index < mountDtos.Count; index++)
        {
            mounts.Add(BuildMount(mountDtos[index]!, index, id!, capabilities));
        }

        var descriptor = new PluginDescriptor
        {
            Id = id!,
            Name = name!,
            Version = string.IsNullOrWhiteSpace(manifest.Version) ? "1.0.0" : manifest.Version.Trim(),
            Description = manifest.Description?.Trim() ?? string.Empty,
            Icon = icon,
            MinHostVersion = string.IsNullOrWhiteSpace(manifest.MinHostVersion) ? null : manifest.MinHostVersion.Trim(),
            Capabilities = capabilities,
            Mounts = mounts,
            Source = source,
            RootPath = Path.GetFullPath(pluginRoot),
            State = state,
            DisabledReason = disabledReason,
            Issues = pluginIssues
        };

        return new PluginValidationResult(descriptor, []);
    }

    private PluginCapability BuildCapability(PluginCapabilityDto dto, int index, string pluginRoot)
    {
        var issues = new List<PluginIssue>();
        var path = $"capabilities[{index}]";

        var id = dto.Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            issues.Add(Error("缺少 id", $"{path}.id"));
            id = string.Empty;
        }
        else if (!IdPattern.IsMatch(id))
        {
            issues.Add(Error("id 必须匹配 ^[a-z][a-z0-9-]{1,31}$", $"{path}.id"));
        }

        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            issues.Add(Warning("缺少 name，暂时用 id 代替", $"{path}.name"));
            name = id;
        }

        var parameters = BuildParameters(dto.Parameters, index, issues);
        var parameterKeys = parameters.Select(parameter => parameter.Key).ToHashSet(StringComparer.Ordinal);

        string? skillContent = null;
        string skillRelative = dto.Skill?.Trim() ?? string.Empty;
        var skillIssue = TryLoadSkill(pluginRoot, dto.Skill, out var fullSkillPath, out skillContent);
        if (skillIssue is not null)
        {
            issues.Add(skillIssue);
        }
        else if (fullSkillPath is not null)
        {
            skillRelative = Path.GetRelativePath(pluginRoot, fullSkillPath).Replace('\\', '/');
            ValidatePlaceholders(skillContent!, skillRelative, parameterKeys, issues);
        }

        var output = PluginOutputFormat.Text;
        if (dto.Output?.Format is { } format)
        {
            switch (format.Trim().ToLowerInvariant())
            {
                case "text":
                    output = PluginOutputFormat.Text;
                    break;
                case "segments":
                    output = PluginOutputFormat.Segments;
                    break;
                default:
                    issues.Add(Error($"output.format 只能是 text 或 segments，实际是 \"{format}\"", $"{path}.output.format"));
                    break;
            }
        }

        var (skillName, skillDescription, skillVersion) = ParseFrontmatterValues(skillContent);

        return new PluginCapability
        {
            Id = id!,
            Name = name!,
            SkillPath = skillRelative,
            SkillContent = skillContent,
            SkillName = skillName,
            SkillDescription = skillDescription,
            SkillVersion = skillVersion,
            AllowGlobalSkills = dto.AllowGlobalSkills ?? false,
            AllowHistory = dto.AllowHistory ?? false,
            OutputFormat = output,
            SendsText = dto.Privacy?.SendsText ?? false,
            Parameters = parameters,
            Issues = issues
        };
    }

    private List<PluginParameter> BuildParameters(List<PluginParameterDto>? dtos, int capabilityIndex, List<PluginIssue> issues)
    {
        var parameters = new List<PluginParameter>();
        if (dtos is null)
        {
            return parameters;
        }

        for (var index = 0; index < dtos.Count; index++)
        {
            var dto = dtos[index];
            if (dto is null)
            {
                continue;
            }

            var path = $"capabilities[{capabilityIndex}].parameters[{index}]";
            var key = dto.Key?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                issues.Add(Error("参数缺少 key", $"{path}.key"));
                continue;
            }

            if (!ParameterKeyPattern.IsMatch(key))
            {
                issues.Add(Error($"参数 key \"{key}\" 必须匹配 ^[a-z][a-zA-Z0-9_]*$", $"{path}.key"));
                continue;
            }

            if (key.StartsWith("ui.", StringComparison.Ordinal))
            {
                issues.Add(Warning($"参数 key \"{key}\" 使用了保留前缀 ui.，建议换一个", $"{path}.key"));
            }

            if (parameters.Any(parameter => string.Equals(parameter.Key, key, StringComparison.Ordinal)))
            {
                issues.Add(Error($"参数 key \"{key}\" 重复", $"{path}.key"));
                continue;
            }

            if (!TryParseKind(dto.Kind, out var kind))
            {
                issues.Add(Error($"参数 \"{key}\" 的 kind 必须是 select / multiselect / toggle / text / number", $"{path}.kind"));
                continue;
            }

            var label = dto.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                issues.Add(Warning($"参数 \"{key}\" 缺少 label，暂时用 key 代替", $"{path}.label"));
                label = key;
            }

            var options = BuildOptions(dto.Options, kind, key, path, issues);

            var defaultValue = dto.Default?.Trim();
            if (kind == PluginParameterKind.Number && !string.IsNullOrEmpty(defaultValue) && !NumberPattern.IsMatch(defaultValue))
            {
                issues.Add(Error($"参数 \"{key}\" 的 default \"{defaultValue}\" 不是合法数字", $"{path}.default"));
                defaultValue = null;
            }

            if (kind == PluginParameterKind.Toggle && !string.IsNullOrEmpty(defaultValue)
                && !bool.TryParse(defaultValue, out _))
            {
                issues.Add(Error($"参数 \"{key}\" 的 default 只能是 true 或 false", $"{path}.default"));
                defaultValue = null;
            }

            if (options.Count > 0 && !string.IsNullOrEmpty(defaultValue)
                && !options.Any(option => string.Equals(option.Value, defaultValue, StringComparison.Ordinal)))
            {
                // 不算错误：运行时会回落到第一个 option，这里只提示作者。
                issues.Add(Warning($"参数 \"{key}\" 的 default \"{defaultValue}\" 不在 options 里", $"{path}.default"));
            }

            var defaultFrom = dto.DefaultFrom?.Trim();
            if (!string.IsNullOrEmpty(defaultFrom)
                && !SupportedDefaultFrom.Contains(defaultFrom, StringComparer.Ordinal))
            {
                issues.Add(Error($"参数 \"{key}\" 的 defaultFrom \"{defaultFrom}\" 不支持（可用：system-language / system-locale）", $"{path}.defaultFrom"));
            }

            var maxLength = dto.MaxLength ?? 2000;
            if (kind == PluginParameterKind.Text && maxLength <= 0)
            {
                issues.Add(Warning($"参数 \"{key}\" 的 maxLength 必须大于 0，已改为 2000", $"{path}.maxLength"));
                maxLength = 2000;
            }

            parameters.Add(new PluginParameter
            {
                Key = key,
                Label = label,
                Kind = kind,
                Options = options,
                Default = defaultValue,
                DefaultFrom = defaultFrom,
                Fallback = dto.Fallback?.Trim(),
                Required = dto.Required ?? true,
                Placeholder = dto.Placeholder,
                MaxLength = maxLength
            });
        }

        return parameters;
    }

    private static List<PluginParameterOption> BuildOptions(
        List<PluginParameterOptionDto>? dtos,
        PluginParameterKind kind,
        string key,
        string path,
        List<PluginIssue> issues)
    {
        var options = new List<PluginParameterOption>();
        var requiresOptions = kind is PluginParameterKind.Select or PluginParameterKind.Multiselect;

        if (dtos is null || dtos.Count == 0)
        {
            if (requiresOptions)
            {
                issues.Add(Error($"参数 \"{key}\" 是 {kind}，必须提供 options", $"{path}.options"));
            }

            return options;
        }

        for (var index = 0; index < dtos.Count; index++)
        {
            var dto = dtos[index];
            var optionPath = $"{path}.options[{index}]";
            var value = dto?.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                issues.Add(Error($"options[{index}] 缺少 value", $"{optionPath}.value"));
                continue;
            }

            if (options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
            {
                issues.Add(Warning($"options 里 \"{value}\" 重复", $"{optionPath}.value"));
                continue;
            }

            var label = dto!.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                issues.Add(Warning($"options[{index}] 缺少 label，暂时用 value 代替", $"{optionPath}.label"));
                label = value;
            }

            options.Add(new PluginParameterOption(value, label));
        }

        if (requiresOptions && options.Count == 0)
        {
            issues.Add(Error($"参数 \"{key}\" 是 {kind}，但没有任何可用 option", $"{path}.options"));
        }

        return options;
    }

    private PluginMount BuildMount(
        PluginMountDto dto,
        int index,
        string pluginId,
        IReadOnlyList<PluginCapability> capabilities)
    {
        var issues = new List<PluginIssue>();
        var path = $"mounts[{index}]";

        var id = dto.Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            issues.Add(Error("缺少 id", $"{path}.id"));
            id = string.Empty;
        }
        else if (!IdPattern.IsMatch(id))
        {
            issues.Add(Error("id 必须匹配 ^[a-z][a-z0-9-]{1,31}$", $"{path}.id"));
        }

        var surface = PluginSurface.Composer;
        if (!TryParseSurface(dto.Surface, out surface))
        {
            issues.Add(Error($"surface 必须是 sidebar / composer / message，实际是 \"{dto.Surface}\"", $"{path}.surface"));
        }

        var capabilityId = dto.Capability?.Trim() ?? string.Empty;
        var capability = capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, capabilityId, StringComparison.Ordinal));

        if (capability is null)
        {
            issues.Add(Error(
                string.IsNullOrEmpty(capabilityId)
                    ? "缺少 capability"
                    : $"capability \"{capabilityId}\" 在本插件里不存在",
                $"{path}.capability"));
        }
        else if (!capability.IsUsable)
        {
            issues.Add(Error($"capability \"{capabilityId}\" 自身加载失败，这个入口无法使用", $"{path}.capability"));
        }

        var input = PluginMount.DefaultInputFor(surface);
        if (!string.IsNullOrWhiteSpace(dto.Input))
        {
            if (TryParseInput(dto.Input, out var parsed))
            {
                input = parsed;
            }
            else
            {
                issues.Add(Error($"input 必须是 composer / message / selection，实际是 \"{dto.Input}\"", $"{path}.input"));
            }
        }

        return new PluginMount
        {
            PluginId = pluginId,
            Id = id!,
            Surface = surface,
            CapabilityId = capabilityId,
            Input = input,
            Order = dto.Order ?? 100,
            Show = dto.Show ?? true,
            Capability = capability,
            Issues = issues
        };
    }

    private PluginIcon? BuildIcon(PluginIconDto? dto, List<PluginIssue> issues)
    {
        if (dto is null)
        {
            return null;
        }

        var text = dto.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }

        var rawPaths = dto.Paths?.Where(item => !string.IsNullOrWhiteSpace(item)).ToList() ?? [];
        if (rawPaths.Count == 0)
        {
            if (text is null)
            {
                issues.Add(Warning("icon 既没有 paths 也没有 text，已忽略", "icon"));
                return null;
            }

            return new PluginIcon([], text);
        }

        if (rawPaths.Count > PluginIcon.MaxPaths)
        {
            issues.Add(Warning($"icon.paths 最多 {PluginIcon.MaxPaths} 条，已忽略图标", "icon.paths"));
            return text is null ? null : new PluginIcon([], text);
        }

        var valid = new List<string>();
        var totalLength = 0;
        for (var index = 0; index < rawPaths.Count; index++)
        {
            var data = rawPaths[index]!.Trim();
            if (!IconPathPattern.IsMatch(data))
            {
                issues.Add(Warning($"icon.paths[{index}] 含非法字符，已忽略这条 path（只接受 SVG path 数据）", $"icon.paths[{index}]"));
                continue;
            }

            totalLength += data.Length;
            valid.Add(data);
        }

        if (totalLength > PluginIcon.MaxLength)
        {
            issues.Add(Warning($"icon.paths 总长超过 {PluginIcon.MaxLength} 字符，已忽略图标", "icon.paths"));
            valid.Clear();
        }

        if (valid.Count == 0 && text is null)
        {
            return null;
        }

        return new PluginIcon(valid, text);
    }

    /// <summary>解析并读取 skill；返回的 issue 不为 null 表示这份技能不能用。</summary>
    private static PluginIssue? TryLoadSkill(
        string pluginRoot,
        string? skill,
        out string? fullPath,
        out string? content)
    {
        fullPath = null;
        content = null;

        if (string.IsNullOrWhiteSpace(skill))
        {
            return Error("缺少 skill 路径");
        }

        var normalized = skill.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            return Error("skill 路径越界：不允许绝对路径", "skill");
        }

        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            return Error("skill 路径越界：不允许 ..", "skill");
        }

        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return Error("skill 必须是 .md 文件", "skill");
        }

        var root = Path.GetFullPath(pluginRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, normalized));
        }
        catch (ArgumentException)
        {
            return Error("skill 路径非法", "skill");
        }

        if (!candidate.StartsWith(rootPrefix, PathComparison))
        {
            return Error("skill 路径越界：必须位于插件目录内", "skill");
        }

        if (!File.Exists(candidate))
        {
            return Error($"skill 文件不存在：{normalized}", "skill");
        }

        try
        {
            var info = new FileInfo(candidate);
            if (info.Length > MaxSkillBytes)
            {
                return Error($"skill 超过 {MaxSkillBytes / 1024} KB 上限", "skill");
            }

            content = File.ReadAllText(candidate);
            fullPath = candidate;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error($"skill 读取失败：{ex.Message}", "skill");
        }
    }

    /// <summary>占位符必须都能在参数里找到定义；否则能力不可用，避免运行时空值。</summary>
    private static void ValidatePlaceholders(
        string content,
        string skillFile,
        HashSet<string> parameterKeys,
        List<PluginIssue> issues)
    {
        var lines = SplitLines(content);

        // 占位符只校验正文：frontmatter 是元数据，不参与模板替换。
        ParseFrontmatter(lines, out var bodyStart);

        for (var index = bodyStart; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!line.Contains("{{", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in RawPlaceholderPattern.Matches(line))
            {
                if (!PlaceholderPattern.IsMatch(match.Value))
                {
                    issues.Add(new PluginIssue(
                        PluginIssueSeverity.Warning,
                        $"无法识别的占位符 {match.Value}（写法只支持 {{{{key}}}} 与 {{{{key.label}}}}）",
                        skillFile,
                        index + 1));
                    continue;
                }

                var key = PlaceholderPattern.Match(match.Value).Groups[1].Value;
                if (!parameterKeys.Contains(key))
                {
                    issues.Add(new PluginIssue(
                        PluginIssueSeverity.Error,
                        $"占位符 {{{{{key}}}}} 没有对应的参数定义",
                        skillFile,
                        index + 1));
                }
            }
        }
    }

    /// <summary>解析 frontmatter，返回 name / description / version，并给出正文起始行（0 起始）。</summary>
    internal static SkillFrontmatter ParseFrontmatter(IReadOnlyList<string> lines, out int bodyStartIndex)
    {
        bodyStartIndex = 0;

        var first = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim().Length > 0)
            {
                first = index;
                break;
            }
        }

        if (first < 0 || lines[first].Trim() != "---")
        {
            return SkillFrontmatter.Empty;
        }

        var end = -1;
        for (var index = first + 1; index < lines.Count; index++)
        {
            if (lines[index].Trim() == "---")
            {
                end = index;
                break;
            }
        }

        if (end < 0)
        {
            // 只有开头没有结束：当作没有 frontmatter，正文从第一行算起。
            return SkillFrontmatter.Empty;
        }

        string? name = null;
        string? description = null;
        string? version = null;

        for (var index = first + 1; index < end; index++)
        {
            var line = lines[index].Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'', '“', '”');

            switch (key.ToLowerInvariant())
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "version":
                    version = value;
                    break;
            }
        }

        bodyStartIndex = end + 1;
        return new SkillFrontmatter(name, description, version);
    }

    private static (string? Name, string? Description, string? Version) ParseFrontmatterValues(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return (null, null, null);
        }

        var frontmatter = ParseFrontmatter(SplitLines(content), out _);
        return (frontmatter.Name, frontmatter.Description, frontmatter.Version);
    }

    private static List<string> SplitLines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static bool TryParseKind(string? text, out PluginParameterKind kind)
    {
        kind = PluginParameterKind.Select;
        if (string.IsNullOrWhiteSpace(text) || !text.All(char.IsLetter))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out kind);
    }

    private static bool TryParseSurface(string? text, out PluginSurface surface)
    {
        surface = PluginSurface.Composer;
        if (string.IsNullOrWhiteSpace(text) || !text.All(char.IsLetter))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out surface);
    }

    private static bool TryParseInput(string? text, out PluginInput input)
    {
        input = PluginInput.Composer;
        if (string.IsNullOrWhiteSpace(text) || !text.All(char.IsLetter))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out input);
    }

    private static string TruncateByTextElements(string value, int max)
    {
        var index = 0;
        var count = 0;
        while (index < value.Length && count < max)
        {
            index += char.IsHighSurrogate(value[index]) && index + 1 < value.Length ? 2 : 1;
            count++;
        }

        return value[..index];
    }

    private static PluginIssue Error(string message, string? path = null) =>
        new(PluginIssueSeverity.Error, message, PluginManifestJson.FileName, Path: path);

    private static PluginIssue Warning(string message, string? path = null) =>
        new(PluginIssueSeverity.Warning, message, PluginManifestJson.FileName, Path: path);
}
