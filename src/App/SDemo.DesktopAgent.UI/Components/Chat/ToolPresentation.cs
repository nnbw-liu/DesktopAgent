using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Components.Chat;

/// <summary>工具卡的形态；决定用哪种卡片渲染，而不是决定数据。</summary>
public enum ToolCardKind
{
    Generic,
    Terminal,
    File,
    Diff,
    Search,
    Web,
    Skill
}

/// <summary>
/// 一条工具轨迹在界面上的呈现。由客户端从「工具名 + 参数预览 + 结果预览 + 失败状态」现场算出来，
/// 服务端不存渲染好的卡片——新增工具只要落进 <see cref="ToolPresentation"/> 就行，不用改模板。
/// </summary>
public sealed record ToolCard(
    ToolCardKind Kind,
    string ToolId,
    string Title,
    string? Subtitle,
    string? Summary,
    IReadOnlyList<EditedFile>? Edits,
    string? ArgsJson,
    string? ResultPreview)
{
    /// <summary>展开后有没有可看的内容（没有就不显示展开箭头）。</summary>
    public bool HasDetail =>
        !string.IsNullOrWhiteSpace(ArgsJson)
        || !string.IsNullOrWhiteSpace(ResultPreview)
        || Edits is { Count: > 0 };
}

/// <summary>工具行的纯展示映射：工具 id → 卡片形态 + 一行标题 + 状态文案。</summary>
public static class ToolPresentation
{
    /// <summary>把一条轨迹映射成卡片描述。</summary>
    public static ToolCard Describe(ToolTraceEntry entry)
    {
        var kind = KindOf(entry.ToolId);
        var title = FirstNonEmpty(entry.Title, entry.ArgsDigest, entry.ToolId)!;
        var subtitle = SubtitleOf(kind, entry);

        return new ToolCard(
            kind,
            entry.ToolId,
            title,
            subtitle,
            entry.Summary,
            entry.Edits,
            entry.ArgsJson,
            entry.ResultPreview);
    }

    /// <summary>工具 id → 卡片形态。未知工具一律 generic，永不空白。</summary>
    public static ToolCardKind KindOf(string toolId) => toolId switch
    {
        "run_command" or "run_powershell" => ToolCardKind.Terminal,
        "replace_text" or "apply_patch" => ToolCardKind.Diff,
        "read_file" or "write_file" or "list_tree" or "list_dir" => ToolCardKind.File,
        "search_files" or "glob_files" or "search_workspace_memory" or "search_capabilities" => ToolCardKind.Search,
        "web_search" or "web_fetch" => ToolCardKind.Web,
        "load_skill" => ToolCardKind.Skill,
        _ => ToolCardKind.Generic
    };

    /// <summary>状态的 CSS 修饰类。</summary>
    public static string StatusClass(string status) => status switch
    {
        ToolTraceEntry.StatusError => "is-error",
        ToolTraceEntry.StatusDenied or ToolTraceEntry.StatusRejected => "is-rejected",
        ToolTraceEntry.StatusCanceled => "is-canceled",
        ToolTraceEntry.StatusRunning => "is-running",
        _ => "is-ok"
    };

    /// <summary>状态文案（与旧工具面板保持一致，用户已经认识这套说法）。</summary>
    public static string StatusText(ToolTraceEntry entry) => entry.Status switch
    {
        ToolTraceEntry.StatusRunning => "运行中…",
        ToolTraceEntry.StatusOk => entry.ExitCode is 0 or null ? "成功" : $"退出码 {entry.ExitCode}",
        ToolTraceEntry.StatusError => entry.ExitCode is int code ? $"失败（{code}）" : "失败",
        ToolTraceEntry.StatusRejected => "已拒绝",
        ToolTraceEntry.StatusDenied => "未授权",
        ToolTraceEntry.StatusCanceled => "已取消",
        _ => entry.Status
    };

    /// <summary>耗时文案；旧记录没有耗时（-1）时返回空。</summary>
    public static string DurationText(ToolTraceEntry entry) =>
        entry.DurationMs < 0 ? string.Empty
        : entry.DurationMs < 1000 ? $"{entry.DurationMs} ms"
        : $"{entry.DurationMs / 1000.0:0.0} s";

    /// <summary>失败 / 拒绝 / 取消都算"这一步没成功"，用于组头计数。</summary>
    public static bool IsFailure(string status) =>
        status is ToolTraceEntry.StatusError
            or ToolTraceEntry.StatusDenied
            or ToolTraceEntry.StatusRejected
            or ToolTraceEntry.StatusCanceled;

    private static string? SubtitleOf(ToolCardKind kind, ToolTraceEntry entry) => kind switch
    {
        ToolCardKind.Terminal => "命令",
        ToolCardKind.File => "文件",
        ToolCardKind.Diff => entry.Edits is { Count: > 0 } ? "编辑" : "补丁",
        ToolCardKind.Search => "搜索",
        ToolCardKind.Web => "联网",
        ToolCardKind.Skill => "技能",
        _ => null
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
