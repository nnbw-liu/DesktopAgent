using System.IO;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>插件包从哪来：随应用发布的内置插件，或用户自己放进插件目录的本地插件。</summary>
public enum PluginSource
{
    Builtin,
    Local
}

/// <summary>插件能力挂在哪个界面。</summary>
public enum PluginSurface
{
    Sidebar,
    Composer,
    Message,

    /// <summary>在聊天记录里选中文本后出现的浮动插件菜单。</summary>
    Selection
}

/// <summary>Run 的输入从哪来。</summary>
public enum PluginInput
{
    /// <summary>输入框里的文字。</summary>
    Composer,

    /// <summary>某条消息的正文（气泡按钮）。</summary>
    Message,

    /// <summary>聊天记录里选中的文本（划词菜单）。</summary>
    Selection
}

/// <summary>参数控件类型。</summary>
public enum PluginParameterKind
{
    Select,
    Multiselect,
    Toggle,
    Text,
    Number
}

/// <summary>能力产出的文本形态。</summary>
public enum PluginOutputFormat
{
    /// <summary>纯文本。</summary>
    Text,

    /// <summary>按段落成对的原文 / 译文（双语对照）。</summary>
    Segments
}

/// <summary>插件整体状态。加载成功但局部不可用时仍是 <see cref="Available"/>，由各 Capability / Mount 的 Issues 表达。</summary>
public enum PluginState
{
    /// <summary>可用（可能有个别 capability / mount 不可用）。</summary>
    Available,

    /// <summary>被禁用（目前只有 minHostVersion 过高）。</summary>
    Disabled,

    /// <summary>与同 id 的另一个插件重复，已忽略。</summary>
    Duplicate
}

/// <summary>问题的严重程度：Error 会让所在单元不可用，Warning 只提示。</summary>
public enum PluginIssueSeverity
{
    Warning,
    Error
}

/// <summary>
/// 一条加载 / 校验问题。位置信息尽量具体到「文件 + 行」或「文件 + JSON 路径」，
/// 设置页直接展示 <see cref="ToDisplayString"/>，不要再拼装。
/// </summary>
/// <param name="Severity">严重程度。</param>
/// <param name="Message">中文说明。</param>
/// <param name="File">相对插件目录的文件名，例如 <c>plugin.json</c> / <c>skills/translate.md</c>。</param>
/// <param name="Line">1 起始的行号（JSON 语法错误、skill 里的占位符才有）。</param>
/// <param name="Path">JSON 路径，例如 <c>mounts[0].capability</c>。</param>
public sealed record PluginIssue(
    PluginIssueSeverity Severity,
    string Message,
    string? File = null,
    int? Line = null,
    string? Path = null)
{
    public bool IsError => Severity == PluginIssueSeverity.Error;

    /// <summary>「plugin.json 第 12 行：xxx」/「plugin.json › mounts[0].capability：xxx」/「skills/a.md 第 3 行：xxx」。</summary>
    public string ToDisplayString()
    {
        var location = File;
        if (location is not null)
        {
            if (Line is { } line)
            {
                location = $"{location} 第 {line} 行";
            }
            else if (!string.IsNullOrEmpty(Path))
            {
                location = $"{location} › {Path}";
            }
        }

        return location is null ? Message : $"{location}：{Message}";
    }
}

/// <summary>图标只接受 SVG path 数据（不接受整段 SVG），详见设计文档 §13。</summary>
public sealed record PluginIcon(IReadOnlyList<string> Paths, string? Text = null)
{
    /// <summary>最多 8 条 path。</summary>
    public const int MaxPaths = 8;

    /// <summary>path 数据总长上限（4 KB）。</summary>
    public const int MaxLength = 4096;

    public static PluginIcon Empty { get; } = new([], null);

    public bool IsEmpty => Paths.Count == 0 && string.IsNullOrWhiteSpace(Text);
}

/// <summary>一个下拉选项。</summary>
public sealed record PluginParameterOption(string Value, string Label);

/// <summary>参数 schema：输入框内联控件、气泡面板、设置页默认值三处共用。</summary>
public sealed record PluginParameter
{
    /// <summary>模板里的键名，<c>^[a-z][a-zA-Z0-9_]*$</c>。</summary>
    public required string Key { get; init; }

    /// <summary>控件标签。</summary>
    public required string Label { get; init; }

    public required PluginParameterKind Kind { get; init; }

    /// <summary><see cref="PluginParameterKind.Select"/> / <see cref="PluginParameterKind.Multiselect"/> 的候选项。</summary>
    public IReadOnlyList<PluginParameterOption> Options { get; init; } = [];

    /// <summary>字面默认值。</summary>
    public string? Default { get; init; }

    /// <summary>动态默认值来源，目前支持 <c>system-language</c> / <c>system-locale</c>。</summary>
    public string? DefaultFrom { get; init; }

    /// <summary>动态默认值解析不出来时的兜底。</summary>
    public string? Fallback { get; init; }

    public bool Required { get; init; } = true;

    public string? Placeholder { get; init; }

    public int MaxLength { get; init; } = 2000;

    public bool HasOptions => Kind is PluginParameterKind.Select or PluginParameterKind.Multiselect;

    /// <summary>把参数值翻译成显示名（<c>{{key.label}}</c>）；没有匹配项时退回原值。</summary>
    public string ResolveLabel(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Label;
        }

        var option = Options.FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal));
        return option?.Label ?? value;
    }
}

/// <summary>能力：一份 skill + 一组参数 + 输出约定。加载失败时 <see cref="IsUsable"/> 为 false，其余能力不受影响。</summary>
public sealed record PluginCapability
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>相对插件目录的技能路径（已规范化，不允许越界）。</summary>
    public required string SkillPath { get; init; }

    /// <summary>skill 文件全文（含 frontmatter），加载时读取一次。</summary>
    public string? SkillContent { get; init; }

    public string? SkillName { get; init; }

    public string? SkillDescription { get; init; }

    public string? SkillVersion { get; init; }

    /// <summary>是否把全局技能一并注入（默认关）。</summary>
    public bool AllowGlobalSkills { get; init; }

    /// <summary>是否带会话历史（默认关）。</summary>
    public bool AllowHistory { get; init; }

    /// <summary>
    /// 控制「是否关联上下文」的 toggle 参数 key；为空表示历史只由 <see cref="AllowHistory"/> 决定。
    /// 设置了它时，参数值解析为 <c>false</c> 的本轮不带历史。
    /// </summary>
    public string? ContextParameter { get; init; }

    public PluginOutputFormat OutputFormat { get; init; } = PluginOutputFormat.Text;

    /// <summary>会把文本发给模型服务商（用于首次使用提示）。</summary>
    public bool SendsText { get; init; }

    public IReadOnlyList<PluginParameter> Parameters { get; init; } = [];

    public IReadOnlyList<PluginIssue> Issues { get; init; } = [];

    public bool IsUsable => !Issues.Any(issue => issue.IsError);

    /// <summary>按 key 找参数。</summary>
    public PluginParameter? FindParameter(string key) =>
        Parameters.FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.Ordinal));
}

/// <summary>入口：把某个能力挂到某个界面，并声明输入从哪来。</summary>
public sealed record PluginMount
{
    public required string PluginId { get; init; }

    public required string Id { get; init; }

    public required PluginSurface Surface { get; init; }

    public required string CapabilityId { get; init; }

    /// <summary>输入来源；未声明时按 surface 推断（见 <see cref="DefaultInputFor"/>）。</summary>
    public PluginInput Input { get; init; }

    /// <summary>升序，仅同 surface 内比较。</summary>
    public int Order { get; init; } = 100;

    public bool Show { get; init; } = true;

    /// <summary>解析后的能力；为 null 表示 mount.capability 指向不存在的能力。</summary>
    public PluginCapability? Capability { get; init; }

    public IReadOnlyList<PluginIssue> Issues { get; init; } = [];

    /// <summary>组合键 <c>&lt;pluginId&gt;/&lt;mountId&gt;</c>，状态存储与会话记忆都用它。</summary>
    public string Key => $"{PluginId}/{Id}";

    public bool IsUsable =>
        Capability is { IsUsable: true } && !Issues.Any(issue => issue.IsError);

    public static PluginInput DefaultInputFor(PluginSurface surface) => surface switch
    {
        PluginSurface.Message => PluginInput.Message,
        PluginSurface.Selection => PluginInput.Selection,
        _ => PluginInput.Composer
    };
}

/// <summary>一个插件包：目录 + plugin.json，外加加载时的校验结果。</summary>
public sealed record PluginDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Version { get; init; } = "1.0.0";

    public string Description { get; init; } = "";

    public PluginIcon? Icon { get; init; }

    /// <summary>高于当前应用版本时插件被禁用。</summary>
    public string? MinHostVersion { get; init; }

    public IReadOnlyList<PluginCapability> Capabilities { get; init; } = [];

    public IReadOnlyList<PluginMount> Mounts { get; init; } = [];

    public required PluginSource Source { get; init; }

    /// <summary>插件目录的完整路径。</summary>
    public required string RootPath { get; init; }

    public PluginState State { get; init; } = PluginState.Available;

    /// <summary>被禁用时的原因（设置页展示）。</summary>
    public string? DisabledReason { get; init; }

    /// <summary>同 id 的内置插件被本地插件覆盖时，内置那条带这个标记。</summary>
    public bool IsOverridden { get; init; }

    /// <summary>插件级问题（未知字段、图标被拒等）。</summary>
    public IReadOnlyList<PluginIssue> Issues { get; init; } = [];

    public string ManifestPath => Path.Combine(RootPath, "plugin.json");

    /// <summary>目录名（一般等于 id，但不作要求）。</summary>
    public string DirectoryName =>
        Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>没有图标时显示的首字（侧栏 / chip 的兜底）。</summary>
    public string Initial
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Icon?.Text))
            {
                return Icon!.Text!;
            }

            if (Name.Length == 0)
            {
                return "?";
            }

            return char.IsHighSurrogate(Name[0]) && Name.Length > 1 ? Name[..2] : Name[..1];
        }
    }

    /// <summary>排序用的顺序：manifest 没有插件级 order，取入口 order 的最小值。</summary>
    public int Order => Mounts.Count == 0 ? 100 : Mounts.Min(mount => mount.Order);

    /// <summary>能不能真正跑：整体可用、没被覆盖、且至少有一个可用入口。</summary>
    public bool IsUsable =>
        State == PluginState.Available && !IsOverridden && Mounts.Any(mount => mount.IsUsable);

    public bool HasErrors => Issues.Any(issue => issue.IsError);

    /// <summary>被禁用 / 重复时的中文状态标注；正常可用时为 null。</summary>
    public string? StateText => State switch
    {
        PluginState.Disabled => "已禁用",
        PluginState.Duplicate => "重复",
        _ => null
    };

    public PluginCapability? FindCapability(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : Capabilities.FirstOrDefault(capability => string.Equals(capability.Id, id, StringComparison.Ordinal));

    /// <summary>按 surface 取可用入口，已按 order / 名称排序。</summary>
    public IReadOnlyList<PluginMount> UsableMounts(PluginSurface surface) =>
        Mounts
            .Where(mount => mount.Surface == surface && mount.IsUsable)
            .OrderBy(mount => mount.Order)
            .ThenBy(mount => mount.Id, StringComparer.Ordinal)
            .ToList();
}

/// <summary>整个插件都没有解析出来（JSON 语法错误、id 非法、缺少 capabilities 等）。</summary>
public sealed record PluginFailure(
    string DirectoryPath,
    PluginSource Source,
    string? PluginId,
    IReadOnlyList<PluginIssue> Issues)
{
    /// <summary>展示名：优先用清单里读到的 id，否则退回目录名。</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(PluginId)
            ? PluginId!
            : Path.GetFileName(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public bool HasErrors => Issues.Any(issue => issue.IsError);
}

/// <summary>一次扫描的完整结果。</summary>
public sealed class PluginLoadResult
{
    private readonly IReadOnlyList<PluginDescriptor> _usable;

    public PluginLoadResult(
        IReadOnlyList<PluginDescriptor> plugins,
        IReadOnlyList<PluginFailure> failures,
        DateTimeOffset scannedAt,
        IReadOnlyList<string> roots)
    {
        Plugins = plugins;
        Failures = failures;
        ScannedAt = scannedAt;
        Roots = roots;
        _usable = plugins.Where(plugin => plugin.IsUsable).ToList();
    }

    /// <summary>解析成功的插件（含被禁用的、被覆盖的、重复的，便于设置页排错）。</summary>
    public IReadOnlyList<PluginDescriptor> Plugins { get; }

    /// <summary>整个插件加载失败的条目。</summary>
    public IReadOnlyList<PluginFailure> Failures { get; }

    /// <summary>真正能用的插件（已按内置优先 / order / 名称排序）。</summary>
    public IReadOnlyList<PluginDescriptor> Usable => _usable;

    public DateTimeOffset ScannedAt { get; }

    /// <summary>扫描的目录：内置在前，本地在后。</summary>
    public IReadOnlyList<string> Roots { get; }

    public bool IsEmpty => Plugins.Count == 0 && Failures.Count == 0;

    public PluginDescriptor? Find(string? pluginId) =>
        string.IsNullOrEmpty(pluginId)
            ? null
            : _usable.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

    /// <summary>按 <c>&lt;pluginId&gt;/&lt;mountId&gt;</c> 找可用入口。</summary>
    public PluginMount? FindMount(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var separator = key.IndexOf('/');
        if (separator <= 0 || separator == key.Length - 1)
        {
            return null;
        }

        var plugin = Find(key[..separator]);
        var mountId = key[(separator + 1)..];
        return plugin?.Mounts.FirstOrDefault(mount =>
            mount.IsUsable && string.Equals(mount.Id, mountId, StringComparison.Ordinal));
    }

    /// <summary>某个界面上所有可用入口（侧栏 / 输入框 / 气泡都从这里取）。</summary>
    public IReadOnlyList<PluginMount> UsableMounts(PluginSurface surface) =>
        _usable
            .SelectMany(plugin => plugin.UsableMounts(surface))
            .ToList();

    public static PluginLoadResult Empty { get; } =
        new([], [], DateTimeOffset.MinValue, []);
}
