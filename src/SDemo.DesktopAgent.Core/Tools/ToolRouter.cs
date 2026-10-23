using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>P6 工具路由的输入。</summary>
public sealed record ToolSelectionContext(
    string UserInput,
    IReadOnlyList<string> RecentToolIds,
    IReadOnlySet<string> AvailableTools,
    AgentMode Mode);

/// <summary>
/// 关键词 + 最近轨迹的保守路由：Compact 最多 4 个函数；Full 不裁剪。
/// 只在可用工具集合内选择，不会凭空造工具。
/// </summary>
public static class ToolRouter
{
    public const int CompactMaxTools = 4;

    private static readonly string[] EditWords =
        ["改", "修", "加", "重构", "实现", "新增", "删除", "测试", "fix", "add", "implement", "refactor"];

    private static readonly string[] ReadWords =
        ["查", "解释", "在哪", "为什么", "怎么", "what", "where", "why", "explain", "find"];

    private static readonly string[] RunWords =
        ["跑", "构建", "测试", "编译", "build", "test", "run", "compile"];

    public static IReadOnlyList<string> Select(ToolSelectionContext context, int maxTools = CompactMaxTools)
    {
        if (context.Mode == AgentMode.Full)
        {
            return context.AvailableTools.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        var limit = Math.Clamp(maxTools, 1, CompactMaxTools);
        var text = context.UserInput.ToLowerInvariant();

        var preferred = new List<string>();

        if (EditWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
        {
            // 小模型优先用「精确替换」；apply_patch 失败后还有 write_file 兜底。
            preferred.AddRange(["read_file", "replace_text", "apply_patch", "write_file"]);
        }
        else if (RunWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
        {
            preferred.AddRange(["run_command", "read_file"]);
        }
        else if (ReadWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
        {
            preferred.AddRange(["search_files", "read_file", "list_tree"]);
        }
        else
        {
            preferred.AddRange(["list_tree", "read_file", "search_files", "run_command"]);
        }

        // Compact 下优先复用最近用过的工具，减少工具切换。
        foreach (var recent in context.RecentToolIds.Reverse())
        {
            if (context.AvailableTools.Contains(recent) && !preferred.Contains(recent))
            {
                preferred.Insert(0, recent);
            }
        }

        return preferred
            .Where(context.AvailableTools.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }
}
