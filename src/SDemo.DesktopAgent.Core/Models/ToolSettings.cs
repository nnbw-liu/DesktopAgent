namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 工具包的用户开关。键是 <c>AgentToolPackDescriptor.Id</c>（小写）；
/// 没有记录的包按描述里的 <c>DefaultEnabled</c> 处理，所以升级新增的工具默认关闭。
/// </summary>
public sealed class ToolSettings
{
    /// <summary>包 id → 是否启用。空表示全部按默认值。</summary>
    public Dictionary<string, bool> EnabledPacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPackEnabled(string packId, bool defaultEnabled)
    {
        var normalized = Normalize(packId);

        if (EnabledPacks.TryGetValue(normalized, out var enabled))
        {
            return enabled;
        }

        // 从旧版手写 settings.json 读入的键大小写可能不规范；这里兜底，
        // 避免“设置里打开了、运行时却当成关闭”。
        foreach (var pair in EnabledPacks)
        {
            if (string.Equals(pair.Key, packId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return defaultEnabled;
    }

    public void SetPackEnabled(string packId, bool enabled) => EnabledPacks[Normalize(packId)] = enabled;

    private static string Normalize(string packId) => packId.Trim().ToLowerInvariant();
}
