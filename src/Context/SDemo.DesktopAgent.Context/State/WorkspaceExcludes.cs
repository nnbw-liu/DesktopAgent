namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 工作区扫描的排除目录（构建产物、依赖、IDE 临时目录）。
/// 树快照、`@` 引用索引、上下文快照的 workspace 段共用同一份表 —— 三处各写一份一定会漂移。
/// </summary>
public static class WorkspaceExcludes
{
    public static readonly IReadOnlySet<string> Default = new HashSet<string>(
        ["bin", "obj", "node_modules", ".git", ".vs", ".tmp", ".idea", "dist", "target", ".agent"],
        StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Default.Contains(name);
}
