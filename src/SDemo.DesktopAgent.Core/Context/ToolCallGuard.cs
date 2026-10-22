namespace SDemo.DesktopAgent.Context;

/// <summary>工具循环保护结果；<see cref="Allowed"/>=false 时不执行工具，直接把提示回给模型。</summary>
public sealed record ToolCallGuardDecision(
    bool Allowed,
    string? Message,
    int TotalCalls,
    int ConsecutiveRepeats);

/// <summary>
/// P5 循环保护：同一 (tool, args) 连续 3 次给出提醒；单轮总调用达到 8 次后拒绝继续调用。
/// 只统计，不执行任何工具；计数按一次生成（Turn）新建。
/// </summary>
public sealed class ToolCallGuard
{
    public const int MaxCalls = 8;
    public const int RepeatWarningAt = 3;

    private string? _lastKey;
    private int _repeats;

    public int TotalCalls { get; private set; }

    public int LoopsPrevented { get; private set; }

    public ToolCallGuardDecision Check(string toolId, string argumentsDigest)
    {
        var key = toolId + "\n" + argumentsDigest;
        _repeats = string.Equals(_lastKey, key, StringComparison.Ordinal) ? _repeats + 1 : 1;
        _lastKey = key;
        TotalCalls++;

        if (TotalCalls > MaxCalls)
        {
            LoopsPrevented++;
            return new ToolCallGuardDecision(
                false,
                $"本轮工具调用已达 {MaxCalls} 次上限，禁止继续调用；请立即用文本总结当前进展、未完成项与风险。",
                TotalCalls,
                _repeats);
        }

        if (_repeats == RepeatWarningAt)
        {
            return new ToolCallGuardDecision(
                true,
                $"同一工具调用已连续出现 {_repeats} 次（{toolId}）。请换策略，或先总结当前结果再继续。",
                TotalCalls,
                _repeats);
        }

        return new ToolCallGuardDecision(true, null, TotalCalls, _repeats);
    }
}
