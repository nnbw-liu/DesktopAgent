using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>P6 双模：完整工具面 / 精简工具面。</summary>
public enum AgentMode
{
    Full,
    Compact
}

/// <summary>用户偏好；Auto 时按模型档案推断。</summary>
public enum AgentModePreference
{
    Auto,
    Full,
    Compact
}

/// <summary>
/// 模式判定：显式覆盖优先；8K / 16K 窗口、无工具能力、小模型名称标记都判为 Compact。
/// 这里只做判定，不读文件、不调模型。
/// </summary>
public static class AgentModeResolver
{
    public const int CompactWindowThreshold = 16 * 1024;

    private static readonly string[] SmallModelMarkers =
        ["3b", "1.5b", "tiny", "mini", "4b", "7b"];

    public static AgentMode Resolve(
        ModelProfile? profile,
        string? modelId,
        AgentModePreference preference = AgentModePreference.Auto)
    {
        if (preference == AgentModePreference.Full)
        {
            return AgentMode.Full;
        }

        if (preference == AgentModePreference.Compact)
        {
            return AgentMode.Compact;
        }

        profile ??= ModelProfile.Unknown;

        if (profile.ContextWindow <= CompactWindowThreshold)
        {
            return AgentMode.Compact;
        }

        if (!profile.SupportsTools)
        {
            return AgentMode.Compact;
        }

        var name = modelId?.ToLowerInvariant() ?? string.Empty;
        return SmallModelMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal))
            ? AgentMode.Compact
            : AgentMode.Full;
    }
}
