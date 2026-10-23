using System.Globalization;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 参数取值：会话记忆 → default → defaultFrom（系统语言 / 区域）→ fallback → 第一个 option。
/// 记忆或默认值不在 options 里时视为“没有”，继续按后面的顺序回落，最终落到第一个 option，
/// 这样用户改过插件选项后旧记忆不会把会话卡死。
/// </summary>
public sealed class PluginParameterResolver
{
    public const string SystemLanguage = "system-language";
    public const string SystemLocale = "system-locale";

    /// <summary>输出模式（对照 / 纯译文）的记忆键后缀，前缀 <c>ui.</c> 保留给宿主。</summary>
    public const string OutputModeKey = "ui.mode";

    /// <summary>会话记忆的完整键：<c>&lt;pluginId&gt;/&lt;mountId&gt;/&lt;key&gt;</c>。</summary>
    public static string MemoryKey(string pluginId, string mountId, string key) =>
        $"{pluginId}/{mountId}/{key}";

    /// <summary>解析一个能力的全部参数值。</summary>
    public IReadOnlyDictionary<string, string> Resolve(
        PluginCapability capability,
        string pluginId,
        string mountId,
        IReadOnlyDictionary<string, string> memory)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in capability.Parameters)
        {
            values[parameter.Key] = Resolve(parameter, pluginId, mountId, memory);
        }

        return values;
    }

    /// <summary>解析单个参数值。</summary>
    public string Resolve(
        PluginParameter parameter,
        string pluginId,
        string mountId,
        IReadOnlyDictionary<string, string> memory)
    {
        if (memory.TryGetValue(MemoryKey(pluginId, mountId, parameter.Key), out var remembered)
            && IsAcceptableValue(parameter, remembered))
        {
            return remembered;
        }

        if (IsAcceptableValue(parameter, parameter.Default))
        {
            return parameter.Default!;
        }

        if (!string.IsNullOrEmpty(parameter.DefaultFrom))
        {
            var dynamicValue = ResolveDynamicDefault(parameter.DefaultFrom);
            if (IsAcceptableValue(parameter, dynamicValue))
            {
                return dynamicValue!;
            }
        }

        if (IsAcceptableValue(parameter, parameter.Fallback))
        {
            return parameter.Fallback!;
        }

        if (parameter.Options.Count > 0)
        {
            return parameter.Options[0].Value;
        }

        return parameter.Kind == PluginParameterKind.Toggle ? "false" : string.Empty;
    }

    /// <summary>值能不能直接用：null 一律不可用；空串只在非必填时接受；select 必须命中 options。</summary>
    public static bool IsAcceptableValue(PluginParameter parameter, string? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value.Trim().Length == 0)
        {
            return !parameter.Required;
        }

        if (parameter.Options.Count > 0)
        {
            return parameter.Options.Any(option =>
                string.Equals(option.Value, value, StringComparison.Ordinal));
        }

        return parameter.Kind switch
        {
            PluginParameterKind.Toggle => bool.TryParse(value, out _),
            PluginParameterKind.Number => double.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            _ => true
        };
    }

    /// <summary>动态默认值：目前只支持 system-language / system-locale。</summary>
    public static string? ResolveDynamicDefault(string? source) => source switch
    {
        SystemLanguage => MapSystemLanguage(CultureInfo.CurrentUICulture.Name),
        SystemLocale => CultureInfo.CurrentUICulture.Name,
        _ => null
    };

    /// <summary>
    /// 系统区域 → 目标语言代码。桌面与 Android 行为一致（都读 <see cref="CultureInfo.CurrentUICulture"/>）。
    /// 不在表里的区域统一落到 <c>en</c>。
    /// </summary>
    public static string MapSystemLanguage(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return "en";
        }

        var name = cultureName.Trim();

        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // 繁体优先：zh-Hant-* / zh-TW / zh-HK / zh-MO。
            if (name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-Hant";
            }

            // zh-CN / zh-SG / zh-Hans-* / 以及只有 "zh" 的情况。
            return "zh-Hans";
        }

        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }

        if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return "fr";
        }

        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return "ko";
        }

        return "en";
    }
}
