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

/// <summary>技能包内一次引用读取的结果状态。</summary>
public enum SkillFileStatus
{
    /// <summary>读取成功。</summary>
    Ok,

    /// <summary>没有这个技能。</summary>
    SkillNotFound,

    /// <summary>技能存在，但包内没有这个文件。</summary>
    FileNotFound,

    /// <summary>路径越界、绝对路径、含 .. 或重解析点。</summary>
    InvalidPath,

    /// <summary>扩展名不在白名单（.md / .json / .txt）。</summary>
    UnsupportedExtension,

    /// <summary>文件过大，拒绝整篇读入。</summary>
    TooLarge,

    /// <summary>技能是旧式单文件，没有包内引用可读。</summary>
    FlatSkill
}

/// <summary>
/// <see cref="ISkillLibrary.ReadFile"/> 的结果：只回正文和规范化后的相对路径，
/// 错误由 <see cref="Status"/> 区分，工具层据此给模型可恢复的提示。
/// </summary>
public sealed record SkillFileReadResult(
    SkillFileStatus Status,
    string? Content = null,
    string RelativePath = "",
    long Bytes = 0)
{
    public bool IsOk => Status == SkillFileStatus.Ok;

    public static SkillFileReadResult Ok(string content, string relativePath, long bytes) =>
        new(SkillFileStatus.Ok, content, relativePath, bytes);

    public static SkillFileReadResult Fail(SkillFileStatus status, string relativePath = "") =>
        new(status, null, relativePath);
}

/// <summary>
/// 本地技能库的最小契约：索引、正文读取与包内引用读取。
/// 实现由应用层（<c>SkillCatalog</c>）提供，工具包 / 上下文层只依赖这个接口。
/// </summary>
public interface ISkillLibrary
{
    /// <summary>全部技能的索引元数据（按名字排序）。</summary>
    IReadOnlyList<SkillIndexEntry> Index();

    /// <summary>技能正文；名字不存在时返回 null。实现必须防止路径越界。</summary>
    string? ReadContent(string? name);

    /// <summary>
    /// 读取技能包内的一个引用文件（只允许 .md / .json / .txt）。
    /// <paramref name="path"/> 是相对技能包根目录的路径；实现负责路径 jail。
    /// 单文件技能没有包内文件，返回 <see cref="SkillFileStatus.FlatSkill"/>。
    /// </summary>
    SkillFileReadResult ReadFile(string? name, string? path);
}
