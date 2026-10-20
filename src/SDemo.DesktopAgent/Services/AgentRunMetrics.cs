using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一次生成过程的统计：token 用量、工具调用次数。
/// ChatRoom 创建后传给 AgentChatService，流结束后把结果写回消息。
/// </summary>
public sealed class AgentRunMetrics
{
    private readonly Dictionary<string, int> _toolCalls = new(StringComparer.OrdinalIgnoreCase);

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public IReadOnlyDictionary<string, int> ToolCalls => _toolCalls;

    public void RecordToolCall(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _toolCalls[name] = _toolCalls.TryGetValue(name, out var count) ? count + 1 : 1;
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
