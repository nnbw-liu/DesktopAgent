using System.Text;

namespace SDemo.DesktopAgent.Context;

/// <summary>状态包渲染结果；<see cref="Truncated"/> 表示为了预算删过条目。</summary>
public sealed record StatePackRender(string Text, int Tokens, bool Truncated, int TreeEntries, int Edits, int Actions);

/// <summary>
/// 工作区状态包的渲染（P2 §3.3）。超预算时按「结构 → 最近改动 → 最近动作」顺序截断，
/// 验证结果永远保留（它最重要）。
/// </summary>
public static class StatePackFormatter
{
    /// <summary>默认排除的目录（构建产物、依赖、IDE 临时目录）。</summary>
    public static readonly IReadOnlySet<string> DefaultExcludes = new HashSet<string>(
        ["bin", "obj", "node_modules", ".git", ".vs", ".tmp", ".idea", "dist", "target"],
        StringComparer.OrdinalIgnoreCase);

    public static StatePackRender Format(WorkspaceState state, int tokenCap, ITokenCounter counter)
    {
        var cap = Math.Max(128, tokenCap);
        var tree = state.Tree.Take(WorkspaceStateProvider.MaxTreeEntries).ToList();
        var edits = state.RecentEdits.Take(WorkspaceStateProvider.MaxRecentEdits).ToList();
        var actions = state.RecentActions.Take(WorkspaceStateProvider.MaxRecentActions).ToList();
        var truncated = false;

        var text = Render(state, tree, edits, actions, truncated);
        var tokens = counter.CountMessage("system", text);

        // 截断顺序：结构 → 最近改动 → 最近动作；验证块最后（不参与截断）。
        while (tokens > cap && tree.Count > 0)
        {
            tree.RemoveAt(tree.Count - 1);
            truncated = true;
            text = Render(state, tree, edits, actions, truncated);
            tokens = counter.CountMessage("system", text);
        }

        while (tokens > cap && edits.Count > 0)
        {
            edits.RemoveAt(edits.Count - 1);
            truncated = true;
            text = Render(state, tree, edits, actions, truncated);
            tokens = counter.CountMessage("system", text);
        }

        while (tokens > cap && actions.Count > 0)
        {
            actions.RemoveAt(actions.Count - 1);
            truncated = true;
            text = Render(state, tree, edits, actions, truncated);
            tokens = counter.CountMessage("system", text);
        }

        return new StatePackRender(text, tokens, truncated, tree.Count, edits.Count, actions.Count);
    }

    private static string Render(
        WorkspaceState state,
        IReadOnlyList<string> tree,
        IReadOnlyList<RecentEdit> edits,
        IReadOnlyList<RecentAction> actions,
        bool truncated)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 工作区");
        builder.Append("根目录：").AppendLine(string.IsNullOrWhiteSpace(state.Root) ? "（未选择工作区）" : state.Root);
        builder.Append("结构（深度 2，前 ").Append(WorkspaceStateProvider.MaxTreeEntries).AppendLine(" 项）：");

        if (tree.Count == 0)
        {
            builder.AppendLine("  （空目录或不可读）");
        }
        else
        {
            foreach (var path in tree)
            {
                builder.AppendLine(RenderTreeLine(path));
            }

            if (truncated)
            {
                builder.AppendLine("  …（结构已截断）");
            }
        }

        builder.AppendLine("最近改动：");

        if (edits.Count == 0)
        {
            builder.AppendLine("  （无）");
        }
        else
        {
            foreach (var edit in edits)
            {
                builder.Append("  ").Append(edit.Path);

                if (!string.IsNullOrWhiteSpace(edit.HashPrefix))
                {
                    builder.Append("（sha256:").Append(edit.HashPrefix)
                        .Append("，").Append(edit.At.ToString("HH:mm")).Append('）');
                }
                else
                {
                    builder.Append("（").Append(edit.At.ToString("HH:mm")).Append('）');
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 最近动作");

        if (actions.Count == 0)
        {
            builder.AppendLine("  （无）");
        }
        else
        {
            foreach (var action in actions)
            {
                builder.Append("  [").Append(action.At.ToString("HH:mm")).Append("] ")
                    .AppendLine(action.Line);
            }
        }

        builder.AppendLine("## 验证");
        builder.AppendLine(string.IsNullOrWhiteSpace(state.LastVerification)
            ? "未运行验证"
            : state.LastVerification);

        return builder.ToString().TrimEnd();
    }

    private static string RenderTreeLine(string path)
    {
        var isDirectory = path.EndsWith('/');
        var trimmed = path.TrimEnd('/');
        var depth = trimmed.Count(character => character == '/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];

        return new string(' ', 2 + depth * 2) + name + (isDirectory ? "/" : string.Empty);
    }
}
