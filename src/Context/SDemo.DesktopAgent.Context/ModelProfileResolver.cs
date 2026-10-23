using System.Text.RegularExpressions;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 把「provider + modelId」解析成 <see cref="ModelProfile"/>（§3.2）。
/// 解析顺序：显式 Profile → 模型名预设 → provider 默认 → 未知按 8K / 2K。
/// 400 自愈学到的窗口由 <see cref="TokenCalibrationStore"/> 单独保存，不覆盖用户显式配置。
/// </summary>
public sealed class ModelProfileResolver
{
    /// <summary>
    /// 常见模型家族 → 档位。这里只给「大概能装多少」的档位，不写厂商具体参数；
    /// 用户可以在设置页对单个模型显式覆盖，或直接选 8K 保守运行。
    /// </summary>
    private static readonly (string Prefix, string Tier)[] FamilyTiers =
    [
        ("gpt-4.1", "128K"),
        ("gpt-4o", "128K"),
        ("gpt-5", "128K"),
        ("o1", "128K"),
        ("o3", "128K"),
        ("o4", "128K"),
        ("claude", "200K"),
        ("gemini", "200K"),
        ("deepseek", "128K"),
        ("qwen", "128K"),
        ("glm", "128K"),
        ("kimi", "128K"),
        ("moonshot", "128K"),
        ("llama-3", "32K"),
        ("mistral", "32K"),
        ("gemma", "8K"),
        ("phi", "8K")
    ];

    /// <summary>模型名里显式写的档位，例如 <c>xxx-32k</c> / <c>xxx-128k</c>。</summary>
    private static readonly Regex NameTierPattern = new(
        @"(?<![a-z0-9])(?<size>\d{1,3})\s*k(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly TokenCalibrationStore? _calibration;

    public ModelProfileResolver(TokenCalibrationStore? calibration = null) => _calibration = calibration;

    /// <summary>解析档案；<paramref name="entry"/> 为模型目录里的条目（可空）。</summary>
    public ModelProfile Resolve(ModelProvider provider, string modelId, ModelEntry? entry = null)
    {
        var profile = ResolveConfiguredOrNull(provider, modelId, entry)
            ?? ApplyLearnedWindow(provider, modelId);

        return profile ?? ModelProfile.Unknown;
    }

    /// <summary>不查 400 学习结果，只看配置与名字（设置页展示「自动」时用它）。</summary>
    public static ModelProfile ResolveConfigured(ModelProvider provider, string modelId, ModelEntry? entry = null) =>
        ResolveConfiguredOrNull(provider, modelId, entry) ?? ModelProfile.Unknown;

    /// <summary>配置能确定档案时返回，否则返回 null（让调用方继续查学到的窗口）。</summary>
    private static ModelProfile? ResolveConfiguredOrNull(ModelProvider provider, string modelId, ModelEntry? entry)
    {
        // 1. 显式 Profile：用户说了算。只选了档位（Preset）时用档位表展开窗口与输出。
        if (entry?.Profile is { } explicitProfile)
        {
            var configured = ModelProfilePresets.FromPreset(explicitProfile.Preset) is { } preset
                ? explicitProfile with
                {
                    ContextWindow = preset.ContextWindow,
                    MaxOutputTokens = preset.MaxOutputTokens,
                    Preset = preset.Preset
                }
                : explicitProfile;

            return Normalize(configured);
        }

        // 2. 模型名预设：先看名字里写死的档位，再看常见家族。
        if (InferFromName(modelId) is { } inferred)
        {
            return inferred;
        }

        // 3. provider 默认。
        if (provider.DefaultProfile is { } providerProfile)
        {
            return Normalize(providerProfile);
        }

        return null;
    }

    /// <summary>只看模型名推断档位；推断不出来返回 null。</summary>
    public static ModelProfile? InferFromName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var name = modelId.Trim().ToLowerInvariant();

        // 名字里显式写了 32k / 128k 之类的，优先用它（数值换算成档位，不认识的按最接近的档位向上取）。
        foreach (Match match in NameTierPattern.Matches(name))
        {
            if (!int.TryParse(match.Groups["size"].Value, out var size) || size <= 0)
            {
                continue;
            }

            var window = size * 1024;
            var tier = ModelProfilePresets.Tiers
                .FirstOrDefault(candidate => candidate.ContextWindow >= window);

            // 超过 200K 的按 200K 处理：预算只会更保守，不会更激进。
            tier ??= ModelProfilePresets.Tiers[^1];
            return new ModelProfile(tier.ContextWindow, tier.MaxOutputTokens, Preset: tier.Preset);
        }

        foreach (var (prefix, preset) in FamilyTiers)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return ModelProfilePresets.FromPreset(preset);
            }
        }

        return null;
    }

    /// <summary>把用户填的档案夹到合理范围，避免 0 / 负数 / 输出比窗口还大。</summary>
    public static ModelProfile Normalize(ModelProfile profile)
    {
        var window = Math.Max(1024, profile.ContextWindow);
        var maxOutput = Math.Clamp(profile.MaxOutputTokens, 256, Math.Max(256, window / 2));

        return profile with
        {
            ContextWindow = window,
            MaxOutputTokens = maxOutput
        };
    }

    private ModelProfile? ApplyLearnedWindow(ModelProvider provider, string modelId)
    {
        if (_calibration?.GetWindowOverride(provider.Id, modelId) is not int learned)
        {
            return null;
        }

        // 400 学到的窗口只调整窗口，输出上限继续用未知模型的保守值。
        return Normalize(new ModelProfile(learned, ModelProfile.Unknown.MaxOutputTokens, Preset: "learned"));
    }
}
