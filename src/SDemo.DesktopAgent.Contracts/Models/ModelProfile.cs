namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 模型能力档案：窗口、输出上限与能力位。
/// 只描述「模型能装多少」，不描述厂商；未知模型按 8K 窗口 / 2K 输出保守处理。
/// 用户在设置里显式填写时随 <see cref="ModelEntry.Profile"/> 落盘，自动推断结果不写回配置。
/// </summary>
public sealed record ModelProfile(
    int ContextWindow = 8192,
    int MaxOutputTokens = 2048,
    bool SupportsTools = true,
    bool SupportsParallelToolCalls = false,
    bool SupportsReasoning = false,
    string? Preset = null)
{
    /// <summary>未知模型的默认档案（§3.2：按 8K / 2K 输出）。</summary>
    public static ModelProfile Unknown { get; } = new();

    /// <summary>8K 及以下的模型按「小模型」处理：保留轮数更少、提示词更短。</summary>
    public bool IsSmallModel => ContextWindow <= 8192;
}

/// <summary>一个可选的档案档位。只给档位，不硬编码任何厂商的具体型号参数。</summary>
public sealed record ModelProfileTier(string Preset, int ContextWindow, int MaxOutputTokens);

/// <summary>
/// 档案档位表：设置页的「自动 / 8K / 32K / 128K / 200K」就是这几个值。
/// 用户选择档位后写入 <see cref="ModelEntry.Profile"/>，以后可单模型覆盖。
/// </summary>
public static class ModelProfilePresets
{
    public const string Auto = "auto";

    public static readonly IReadOnlyList<ModelProfileTier> Tiers =
    [
        new("8K", 8192, 2048),
        new("32K", 32_768, 4096),
        new("128K", 131_072, 8192),
        new("200K", 204_800, 8192)
    ];

    /// <summary>按档位名创建档案；未知档位返回 null（调用方按「自动」处理）。</summary>
    public static ModelProfile? FromPreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return null;
        }

        var tier = Tiers.FirstOrDefault(candidate =>
            string.Equals(candidate.Preset, preset.Trim(), StringComparison.OrdinalIgnoreCase));

        return tier is null
            ? null
            : new ModelProfile(tier.ContextWindow, tier.MaxOutputTokens, Preset: tier.Preset);
    }
}
