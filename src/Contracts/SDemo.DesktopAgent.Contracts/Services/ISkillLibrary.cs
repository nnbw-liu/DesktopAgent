namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 技能索引里的一条（M2）：只放「名字 + 一句话 + 何时用 + 标签」，
/// 正文通过 <c>load_skill</c> 按需读取，默认上下文成本只有索引。
/// </summary>
public sealed record SkillIndexEntry(
    string Name,
    string Description,
    string WhenToUse,
    IReadOnlyList<string> Tags,
    // frontmatter 的 disable-model-invocation：这类技能不进索引、load_skill 也拒绝，
    // 只有用户用 `/name` 显式点名才能加载。
    bool DisableModelInvocation = false)
{
    public SkillIndexEntry(string name, string description)
        : this(name, description, string.Empty, [])
    {
    }
}

/// <summary>
/// 本地技能库的最小契约：索引与正文读取。
/// 实现由应用层（<c>SkillCatalog</c>）提供，工具包 / 上下文层只依赖这个接口。
/// </summary>
public interface ISkillLibrary
{
    /// <summary>全部技能的索引元数据（按文件名为序）。</summary>
    IReadOnlyList<SkillIndexEntry> Index();

    /// <summary>技能正文；名字不存在时返回 null。实现必须防止路径越界。</summary>
    string? ReadContent(string? name);
}
