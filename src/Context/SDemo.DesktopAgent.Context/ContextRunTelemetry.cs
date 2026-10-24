namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次生成的上下文遥测：运行期间由 <see cref="AgentSessionRunner"/> 与
/// <see cref="ContextBudgetChatClient"/> 共同更新，UI 在流结束后读它渲染占用环与提示。
/// 它不落库（P1 只持久化报告里的数值型结论与工具轨迹）。
/// </summary>
public sealed class ContextRunTelemetry
{
    /// <summary>本次实际使用的计划（重试后会被替换）。</summary>
    public ContextPlan? Plan { get; set; }

    /// <summary>本次的报告（重试 / 中途裁剪后会被替换成新记录）。</summary>
    public ContextReport? Report { get; set; }

    /// <summary>实际发出去的 LLM 请求次数（工具循环里每次调用 +1）。</summary>
    public int RequestCount { get; set; }

    /// <summary>这些请求的累计估算输入 token，用于与 usage 校准。</summary>
    public int EstimatedInputTokens { get; set; }

    /// <summary>有多少次请求在中途被裁剪过。</summary>
    public int MidTurnTrimmedRequests { get; set; }

    /// <summary>是否发生过 400 溢出重试。</summary>
    public bool OverflowRetried { get; set; }

    /// <summary>给用户看的一句提示（溢出 / 需压缩 / 中途裁剪）；null 表示不用提示。</summary>
    public string? ContextNotice { get; set; }

    /// <summary>R1.4：本次生成中摘要请求的累计输入 token（可能触发多次压缩 / 重试）。</summary>
    public int SummaryInputTokens { get; set; }

    /// <summary>R1.4：本次生成中摘要请求的累计输出 token。</summary>
    public int SummaryOutputTokens { get; set; }

    /// <summary>记录一次中途裁剪。</summary>
    public void RecordMidTurnTrim()
    {
        MidTurnTrimmedRequests++;

        if (Report is { } report)
        {
            Report = report with { MidTurnTrimmed = true };
        }
    }
}
