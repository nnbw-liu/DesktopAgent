using System.Text;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 工作区段：根目录 + 深度 2 的结构快照。
/// <para>
/// 结构只是快照，随时可能过期，所以标题下面紧跟一句「用工具核对」——否则模型会拿一段
/// 几分钟前的树当事实，去猜一个已经被改名的文件。
/// </para>
/// </summary>
public sealed class WorkspaceContextSection : IAgentContextSection
{
    public string Name => "workspace";

    public string Title => "工作区";

    public int Order => ContextSectionOrders.Workspace;

    public bool Required => false;

    public int MaxTokens => 320;

    public string? Render(ContextSectionInput input)
    {
        if (!input.Settings.StatePackEnabled || input.State is not { } state)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("根目录：")
            .AppendLine(string.IsNullOrWhiteSpace(state.Root) ? "（未选择工作区）" : state.Root);
        builder.AppendLine("（结构是快照，可能已过期；改文件前用 read_file / list_tree 核对。）");
        builder.Append("结构（深度 ").Append(WorkspaceStateProvider.TreeDepth)
            .Append("，前 ").Append(WorkspaceStateProvider.MaxTreeEntries).AppendLine(" 项）：");

        if (state.Tree.Count == 0)
        {
            builder.AppendLine("  （空目录或不可读）");
        }
        else
        {
            foreach (var path in state.Tree.Take(WorkspaceStateProvider.MaxTreeEntries))
            {
                builder.AppendLine(RenderTreeLine(path));
            }
        }

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

/// <summary>最近改动段：写过的文件 + sha256 前缀 + **相对时间**（不是 `HH:mm`，模型要判断新旧）。</summary>
public sealed class ChangesContextSection : IAgentContextSection
{
    public string Name => "changes";

    public string Title => "最近改动";

    public int Order => ContextSectionOrders.Changes;

    public bool Required => false;

    public int MaxTokens => 120;

    public string? Render(ContextSectionInput input)
    {
        if (!input.Settings.StatePackEnabled || input.State is not { } state || state.RecentEdits.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var edit in state.RecentEdits.Take(WorkspaceStateProvider.MaxRecentEdits))
        {
            var age = ContextSectionText.DescribeAge(
                ContextSectionText.Elapsed(input.Now, edit.At) ?? TimeSpan.Zero);

            builder.Append("  ").Append(edit.Path);

            if (!string.IsNullOrWhiteSpace(edit.HashPrefix))
            {
                builder.Append("（sha256:").Append(edit.HashPrefix).Append('，').Append(age).Append('）');
            }
            else
            {
                builder.Append('（').Append(age).Append('）');
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>最近动作段：工具轨迹的回放行，时间同样用相对量。</summary>
public sealed class ActionsContextSection : IAgentContextSection
{
    public string Name => "actions";

    public string Title => "最近动作";

    public int Order => ContextSectionOrders.Actions;

    public bool Required => false;

    public int MaxTokens => 160;

    public string? Render(ContextSectionInput input)
    {
        if (!input.Settings.StatePackEnabled || input.State is not { } state || state.RecentActions.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var action in state.RecentActions.Take(WorkspaceStateProvider.MaxRecentActions))
        {
            var age = ContextSectionText.DescribeAge(
                ContextSectionText.Elapsed(input.Now, action.At) ?? TimeSpan.Zero);

            builder.Append("  [").Append(age).Append("] ").AppendLine(action.Line);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// 验证段：最近一次构建 / 测试的结果。**required**——这条事实一旦丢失，模型就会在
/// 「上次已经失败」的情况下声称成功；所以它不受预算抑制，只有用户关掉整个状态包才会消失。
/// </summary>
public sealed class VerificationContextSection : IAgentContextSection
{
    public string Name => "verification";

    public string Title => "验证";

    public int Order => ContextSectionOrders.Verification;

    public bool Required => true;

    public int MaxTokens => 120;

    public string? Render(ContextSectionInput input)
    {
        if (input.State is not { } state)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(state.LastVerification)
            ? "未运行验证"
            : state.LastVerification;
    }
}

/// <summary>当前计划段：来自会话 Todo 列表；没有计划时整段消失，成本为 0。</summary>
public sealed class PlanContextSection : IAgentContextSection
{
    /// <summary>单段上限（与 P2 现状一致：200 token）。</summary>
    public const int PlanTokenLimit = 200;

    public string Name => "plan";

    public string Title => "当前计划";

    public int Order => ContextSectionOrders.Plan;

    public bool Required => false;

    public int MaxTokens => PlanTokenLimit;

    public string? Render(ContextSectionInput input)
    {
        if (!input.Settings.PlanContextEnabled || input.State?.Todos is not { Count: > 0 } todos)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var item in todos.Take(TodoItem.MaxItems))
        {
            var line = $"{item.Icon} {item.Content}";
            var candidate = builder.ToString() + line;

            // 段内自带截断：只砍计划条目并留下可读标记，不要指望「整段统一截断」把标题也砍掉。
            if (input.Counter.CountMessage("user", candidate) > PlanTokenLimit)
            {
                builder.AppendLine("…（计划已截断）");
                break;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// 时间段：宿主时钟读数（带 IANA 时区与偏移）+ 距上一条模型可见消息的经过时长。
/// <para>
/// **不做节流**：我们每轮只发一条（替换而不是追加），成本恒定；节流只会在 8K 下省几十个
/// token，却让模型误判「这个构建结果是 3 分钟前还是昨天」。
/// </para>
/// </summary>
public sealed class TimeContextSection : IAgentContextSection
{
    public string Name => "time";

    public string Title => "当前时间";

    public int Order => ContextSectionOrders.Time;

    public bool Required => true;

    public int MaxTokens => 80;

    public string? Render(ContextSectionInput input)
    {
        if (!input.Settings.TimeContextEnabled)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("宿主时钟：")
            .AppendLine(ContextSectionText.FormatStamp(input.Now, input.TimeZoneId));

        var elapsed = ContextSectionText.Elapsed(input.Now, input.LastModelVisibleAt);
        builder.Append("距上一条模型可见消息：")
            .AppendLine(elapsed is null ? "unavailable" : ContextSectionText.DescribeSpan(elapsed.Value));

        // 时区拿不准时要求澄清，而不是猜（DSH 的 time-context 同样把它写成指令）。
        if (string.IsNullOrWhiteSpace(input.TimeZoneId)
            || string.Equals(input.TimeZoneId, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("时区无法确定：需要精确时间时先询问用户，不要猜。");
        }

        return builder.ToString().TrimEnd();
    }
}
