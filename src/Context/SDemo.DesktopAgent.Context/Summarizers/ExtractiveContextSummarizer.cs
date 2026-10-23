using System.Text;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 确定性兜底摘要（P2 §3.2）：完全不调用模型，永远成功。
/// 原则是「宁可少写，也不编造」——只复述用户原话、工具轨迹里的路径与结果。
/// </summary>
public sealed class ExtractiveContextSummarizer : IContextSummarizer
{
    private readonly ITokenCounter _counter;

    /// <summary>从轨迹里挑「写入类」工具，用于「已改动」小节。</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "apply_patch", "edit_file", "create_file",
        "replace_in_file", "move_file", "delete_file", "patch_file"
    };

    private const int MaxUserMessages = 3;
    private const int MaxEarlyUserMessages = 4;
    private const int MaxUserChars = 120;
    private const int MaxTraceLines = 20;
    private const int MaxEdits = 8;
    private const int MaxPreviousChars = 300;

    public ExtractiveContextSummarizer(ITokenCounter counter) => _counter = counter;

    public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = Build(request);
        var capped = SummaryFormat.Cap(text, request.MaxTokens, _counter);

        return Task.FromResult(new SummaryResult(capped, UsedModel: false, _counter.Count(capped), Error: null));
    }

    /// <summary>拼兜底摘要；顺序与模板一致，没有内容的小节写「无」。</summary>
    public string Build(SummaryRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(SummaryFormat.FallbackMarker);

        builder.AppendLine(SummaryFormat.Sections[0]);
        var users = request.Chunk
            .Select((message, index) => (Message: message, Order: request.ChunkStartOrder + index))
            .Where(item => item.Message.Role == ChatMessageRole.User)
            .Where(item => !string.IsNullOrWhiteSpace(item.Message.Content))
            .ToList();

        // 兜底摘要必须保住「最早的长期约束」：只取最后 3 条用户消息时，
        // 首块历史里的约束会在模型摘要失败时整段丢失。
        var earliest = users.Take(MaxEarlyUserMessages);
        var latest = users.TakeLast(MaxUserMessages);
        var selected = earliest.Concat(latest)
            .GroupBy(item => item.Order)
            .Select(group => group.First())
            .ToList();

        if (users.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var (message, order) in selected)
            {
                builder.Append("- [m").Append(order).Append("] 用户：")
                    .AppendLine(Truncate(Collapse(message.Content), MaxUserChars));
            }
        }

        var previous = CompactPrevious(request.PreviousSummary);
        if (previous is { Length: > 0 })
        {
            builder.Append("- 此前摘要：").AppendLine(previous);
        }

        builder.AppendLine(SummaryFormat.Sections[1]);
        builder.AppendLine("- 无（自动摘要无法可靠提取）");

        builder.AppendLine(SummaryFormat.Sections[2]);
        var traces = request.Chunk
            .Select((message, index) => (Message: message, Order: request.ChunkStartOrder + index))
            .SelectMany(item => item.Message.ToolTrace.Select(trace => (Trace: trace, item.Order)))
            .TakeLast(MaxTraceLines)
            .ToList();

        if (traces.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var (trace, order) in traces)
            {
                builder.Append("- [m").Append(order).Append("] ")
                    .AppendLine(ToolTraceFormatter.RenderLine(trace));
            }
        }

        builder.AppendLine(SummaryFormat.Sections[3]);
        var edits = new List<(string Path, int Order)>();

        foreach (var (message, index) in request.Chunk.Select((message, index) => (message, index)))
        {
            foreach (var trace in message.ToolTrace)
            {
                if (!WriteTools.Contains(trace.ToolId))
                {
                    continue;
                }

                var path = ResolvePath(trace.ArgsDigest);
                if (path is null)
                {
                    continue;
                }

                var order = request.ChunkStartOrder + index;
                edits.RemoveAll(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                edits.Add((path, order));
            }
        }

        if (edits.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var (path, order) in edits.TakeLast(MaxEdits))
            {
                builder.Append("- ").Append(path).Append(" [m").Append(order).AppendLine("]");
            }
        }

        builder.AppendLine(SummaryFormat.Sections[4]);
        builder.AppendLine("- 无");

        builder.AppendLine(SummaryFormat.Sections[5]);
        builder.AppendLine("- 无");

        return builder.ToString().TrimEnd();
    }

    private static string? ResolvePath(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var path = digest.Trim();

        // sha256:… 表示「参数里没有路径」，不能当成文件路径写进摘要。
        if (path.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path;
    }

    private static string? CompactPrevious(string? previous)
    {
        if (string.IsNullOrWhiteSpace(previous))
        {
            return null;
        }

        var text = string.Join(
            "；",
            previous
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("## ", StringComparison.Ordinal))
                .Where(line => !line.StartsWith(SummaryFormat.FallbackMarker, StringComparison.Ordinal)));

        return text.Length == 0 ? null : Truncate(Collapse(text), MaxPreviousChars);
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var character in text)
        {
            if (character is '\r' or '\n' or '\t' || char.IsWhiteSpace(character))
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
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string text, int maxChars) => text.Length <= maxChars
        ? text
        : string.Concat(text.AsSpan(0, maxChars), "…");
}
