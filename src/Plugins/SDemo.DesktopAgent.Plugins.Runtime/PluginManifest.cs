using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// plugin.json 的 DTO（与领域模型分离，方便清单格式以后带 schemaVersion 演进）。
/// 字段全部是可空 / 原始类型：类型不对时只让对应的 capability 或 mount 失效，而不是整个插件抛异常。
/// </summary>
internal sealed class PluginManifestDto
{
    public int? SchemaVersion { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    public PluginIconDto? Icon { get; set; }

    public string? MinHostVersion { get; set; }

    public List<PluginCapabilityDto>? Capabilities { get; set; }

    public List<PluginMountDto>? Mounts { get; set; }
}

internal sealed class PluginIconDto
{
    public List<string>? Paths { get; set; }

    public string? Text { get; set; }
}

internal sealed class PluginCapabilityDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Skill { get; set; }

    public bool? AllowGlobalSkills { get; set; }

    public bool? AllowHistory { get; set; }

    /// <summary>控制「是否关联上下文」的 toggle 参数 key。</summary>
    public string? ContextParameter { get; set; }

    public PluginOutputDto? Output { get; set; }

    public PluginPrivacyDto? Privacy { get; set; }

    public List<PluginParameterDto>? Parameters { get; set; }
}

internal sealed class PluginOutputDto
{
    public string? Format { get; set; }
}

internal sealed class PluginPrivacyDto
{
    public bool? SendsText { get; set; }
}

internal sealed class PluginParameterDto
{
    public string? Key { get; set; }

    public string? Label { get; set; }

    public string? Kind { get; set; }

    public List<PluginParameterOptionDto>? Options { get; set; }

    public string? Default { get; set; }

    public string? DefaultFrom { get; set; }

    public string? Fallback { get; set; }

    public bool? Required { get; set; }

    public string? Placeholder { get; set; }

    public int? MaxLength { get; set; }
}

internal sealed class PluginParameterOptionDto
{
    public string? Value { get; set; }

    public string? Label { get; set; }
}

internal sealed class PluginMountDto
{
    public string? Id { get; set; }

    public string? Surface { get; set; }

    public string? Capability { get; set; }

    public string? Input { get; set; }

    public int? Order { get; set; }

    public bool? Show { get; set; }
}

/// <summary>一次清单解析的结果。<see cref="Manifest"/> 为 null 表示整个插件读不出来。</summary>
internal sealed record PluginManifestParseResult(PluginManifestDto? Manifest, IReadOnlyList<PluginIssue> Issues)
{
    public bool Success => Manifest is not null;
}

/// <summary>
/// plugin.json 的反序列化：允许 <c>//</c> 注释与尾逗号，字段名大小写不敏感，
/// 未知字段只记警告（方便发现拼写错误，又不会因为多写一个字段就废掉插件）。
/// </summary>
internal static class PluginManifestJson
{
    /// <summary>plugin.json 大小上限。</summary>
    public const long MaxBytes = 512 * 1024;

    public const string FileName = "plugin.json";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>解析清单文本。任何异常都被转换成一条带行号的 <see cref="PluginIssue"/>。</summary>
    public static PluginManifestParseResult Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifestDto>(json, Options);
            if (manifest is null)
            {
                return new PluginManifestParseResult(
                    null,
                    [new PluginIssue(PluginIssueSeverity.Error, "清单内容为空", FileName)]);
            }

            return new PluginManifestParseResult(manifest, []);
        }
        catch (JsonException ex)
        {
            // JsonException.LineNumber 是 0 起始的，转成界面上看的 1 起始。
            var line = ex.LineNumber is { } number ? (int)number + 1 : (int?)null;
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? "JSON 语法错误" : ex.Message;
            return new PluginManifestParseResult(
                null,
                [new PluginIssue(PluginIssueSeverity.Error, $"清单无法解析：{reason}", FileName, line)]);
        }
    }

    /// <summary>找出清单里所有未知字段，只返回警告，不阻断加载。</summary>
    public static IReadOnlyList<PluginIssue> FindUnknownMembers(string json)
    {
        var issues = new List<PluginIssue>();

        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return issues;
            }

            Walk(document.RootElement, Schema.Root, string.Empty, issues);
        }
        catch (JsonException)
        {
            // 语法错误由 Parse 负责报告，这里保持静默。
        }

        return issues;
    }

    private static void Walk(JsonElement element, FieldSpec? spec, string path, List<PluginIssue> issues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when spec is ObjectSpec objectSpec:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    if (!objectSpec.Fields.TryGetValue(property.Name, out var childSpec))
                    {
                        issues.Add(new PluginIssue(
                            PluginIssueSeverity.Warning,
                            $"未知字段 \"{property.Name}\"（已忽略；可能是拼写错误）",
                            FileName,
                            Path: childPath));
                        continue;
                    }

                    if (childSpec is not null)
                    {
                        Walk(property.Value, childSpec, childPath, issues);
                    }
                }

                break;

            case JsonValueKind.Array when spec is ArraySpec arraySpec && arraySpec.Item is not null:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, arraySpec.Item, $"{path}[{index}]", issues);
                    index++;
                }

                break;
        }
    }

    /// <summary>清单结构（只用于找未知字段；值类型不在这里校验）。</summary>
    private abstract class FieldSpec;

    private sealed class ObjectSpec : FieldSpec
    {
        public ObjectSpec(params (string Name, FieldSpec? Spec)[] fields)
        {
            Fields = new Dictionary<string, FieldSpec?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, spec) in fields)
            {
                Fields[name] = spec;
            }
        }

        public Dictionary<string, FieldSpec?> Fields { get; }
    }

    private sealed class ArraySpec : FieldSpec
    {
        public ArraySpec(FieldSpec? item) => Item = item;

        public FieldSpec? Item { get; }
    }

    private static class Schema
    {
        // 注意顺序：静态字段按声明顺序初始化，引用其它字段的必须放在后面。
        private static readonly ObjectSpec Parameter = new(
            ("key", null),
            ("label", null),
            ("kind", null),
            ("options", new ArraySpec(new ObjectSpec(("value", null), ("label", null)))),
            ("default", null),
            ("defaultFrom", null),
            ("fallback", null),
            ("required", null),
            ("placeholder", null),
            ("maxLength", null));

        private static readonly ObjectSpec Capability = new(
            ("id", null),
            ("name", null),
            ("skill", null),
            ("allowGlobalSkills", null),
            ("allowHistory", null),
            ("contextParameter", null),
            ("output", new ObjectSpec(("format", null))),
            ("privacy", new ObjectSpec(("sendsText", null))),
            ("parameters", new ArraySpec(Parameter)));

        private static readonly ObjectSpec Mount = new(
            ("id", null),
            ("surface", null),
            ("capability", null),
            ("input", null),
            ("order", null),
            ("show", null));

        public static readonly ObjectSpec Root = new(
            ("schemaVersion", null),
            ("id", null),
            ("name", null),
            ("version", null),
            ("description", null),
            ("minHostVersion", null),
            ("icon", new ObjectSpec(("paths", null), ("text", null))),
            ("capabilities", new ArraySpec(Capability)),
            ("mounts", new ArraySpec(Mount)));
    }
}
