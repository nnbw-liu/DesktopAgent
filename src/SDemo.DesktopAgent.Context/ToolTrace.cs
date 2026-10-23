using System.Text;

namespace SDemo.DesktopAgent.Context;

/// <summary>工具轨迹的回放：紧凑行、受 token 预算约束。</summary>
public static class ToolTraceFormatter
{
    /// <summary>每条助手消息最多存多少条轨迹。</summary>
    public const int MaxEntriesPerMessage = 20;

    /// <summary>单条轨迹的摘要上限（存库时截断，保证单条 JSON ≤ 2KB）。</summary>
    public const int MaxSummaryChars = 400;

    /// <summary>回放时摘要只保留前 N 字符（错误行也要短）。</summary>
    private const int ReplaySummaryChars = 160;

    /// <summary>把一条轨迹渲染成紧凑行（§3.6 的格式）。</summary>
    public static string RenderLine(ToolTraceEntry entry)
    {
        var builder = new StringBuilder("[工具] ");
        builder.Append(entry.ToolId);

        if (!string.IsNullOrWhiteSpace(entry.ArgsDigest))
        {
            builder.Append(' ').Append(entry.ArgsDigest);
        }

        builder.Append(" → ").Append(entry.Status);

        if (entry.ExitCode is int exitCode)
        {
            builder.Append("(exit ").Append(exitCode).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(entry.Summary))
        {
            var summary = CollapseWhitespace(entry.Summary, ReplaySummaryChars);
            builder.Append('：').Append(summary);
        }
        else
        {
            builder.Append('，').Append(FormatChars(entry.OutputChars));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 在给定 token 预算内回放轨迹：从最新往回放，放不下的压成一行说明。
    /// 返回 null 表示「本次不回放轨迹」（预算为 0 或没有轨迹）。
    /// </summary>
    public static ToolTraceRender? Render(IReadOnlyList<ToolTraceEntry> entries, int tokenBudget, ITokenCounter counter)
    {
        if (entries.Count == 0 || tokenBudget <= 0)
        {
            return null;
        }

        var lines = new List<string>();
        var used = 0;
        var omitted = 0;

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var line = RenderLine(entries[index]);
            var cost = counter.CountMessage("assistant", line);

            if (used + cost > tokenBudget)
            {
                omitted++;
                continue;
            }

            used += cost;
            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        // 回放顺序恢复成「旧 → 新」，读起来符合时间线。
        lines.Reverse();

        if (omitted > 0)
        {
            var note = $"[工具] 另有 {omitted} 次调用已省略";
            var noteCost = counter.CountMessage("assistant", note);

            if (used + noteCost <= tokenBudget)
            {
                lines.Add(note);
                used += noteCost;
            }
        }

        return new ToolTraceRender(string.Join('\n', lines), used, omitted);
    }

    private static string CollapseWhitespace(string text, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxChars));
        var lastWasSpace = false;

        foreach (var character in text)
        {
            var isBreak = character is '\r' or '\n' or '\t';

            if (isBreak || char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;

            if (builder.Length >= maxChars)
            {
                builder.Append('…');
                break;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatChars(int chars) => chars >= 1024
        ? $"{chars / 1024.0:F1}K 字符"
        : $"{chars} 字符";
}

/// <summary>轨迹回放结果：文本、消耗 token、省略条数。</summary>
public sealed record ToolTraceRender(string Text, int Tokens, int Omitted);
