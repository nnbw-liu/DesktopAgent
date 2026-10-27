namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 技能库的显示偏好。技能本身仍由 <c>SkillCatalog</c> 从磁盘发现，
/// 这里只记录「哪些技能不在输入框里固定占一个 chip」——技能仍可从 <c>/</c> 菜单调用。
/// </summary>
public sealed class SkillPreferences
{
    /// <summary>技能名（小写）→ 是否在输入框里显示固定 chip。没有记录的技能按显示处理。</summary>
    public Dictionary<string, bool> ComposerVisible { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>没有记录时按「显示」处理；旧的 settings.json 不带该节点时行为不变。</summary>
    public bool IsComposerVisible(string name) =>
        ComposerVisible.Count == 0
        || !ComposerVisible.TryGetValue(Normalize(name), out var visible)
        || visible;

    public void SetComposerVisible(string name, bool visible) =>
        ComposerVisible[Normalize(name)] = visible;

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
