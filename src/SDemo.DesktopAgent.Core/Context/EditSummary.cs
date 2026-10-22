namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次生成的文件改动汇总（P3 §3.7）。数据来自 <see cref="ToolTraceEntry.Edits"/>，
/// 同一路径多次改动按次数累加，不重新扫描磁盘。
/// </summary>
public sealed record EditSummary(IReadOnlyList<EditedFile> Files)
{
    public int TotalAdded => Files.Sum(file => file.Added);

    public int TotalRemoved => Files.Sum(file => file.Removed);

    public bool IsEmpty => Files.Count == 0;

    public static EditSummary FromTrace(IEnumerable<ToolTraceEntry> trace) =>
        new(trace
            .SelectMany(entry => entry.Edits ?? [])
            .GroupBy(edit => edit.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EditedFile(
                group.Key,
                group.Sum(edit => edit.Added),
                group.Sum(edit => edit.Removed)))
            .OrderByDescending(file => file.Added + file.Removed)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray());
}
