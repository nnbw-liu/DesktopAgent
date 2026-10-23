using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一次生成过程的统计：token 用量、工具调用次数、工具轨迹。
/// ChatRoom 创建后传给 AgentSessionRunner，流结束后把结果写回消息。
/// </summary>
public sealed class AgentRunMetrics
{
    private readonly Dictionary<string, int> _toolCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToolTraceEntry> _toolTrace = [];

    /// <summary>本次生成（turn）的 id；checkpoint 按它聚合一次生成里的多次编辑。</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>本轮工具循环保护（P5）。</summary>
    public Context.ToolCallGuard Guard { get; } = new();

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public IReadOnlyDictionary<string, int> ToolCalls => _toolCalls;

    /// <summary>一次生成最多保留的轨迹条数；只影响持久化 / 编辑摘要，不影响模型可见的最近 20 条。</summary>
    public const int MaxStoredEntries = 100;

    /// <summary>本次生成的工具轨迹（最多 <see cref="MaxStoredEntries"/> 条）。</summary>
    public IReadOnlyList<ToolTraceEntry> ToolTrace => _toolTrace;

    public void RecordToolCall(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _toolCalls[name] = _toolCalls.TryGetValue(name, out var count) ? count + 1 : 1;
    }

    /// <summary>追加一条轨迹；超过上限的静默丢弃（只保留最近的部分，前面已有汇总）。</summary>
    public void RecordToolTrace(ToolTraceEntry entry)
    {
        if (_toolTrace.Count >= MaxStoredEntries)
        {
            return;
        }

        _toolTrace.Add(entry);
    }

    public void AddUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        InputTokens += (int)(usage.InputTokenCount ?? 0);
        OutputTokens += (int)(usage.OutputTokenCount ?? 0);
    }
}
