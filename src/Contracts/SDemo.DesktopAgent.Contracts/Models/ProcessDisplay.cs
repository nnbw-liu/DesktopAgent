namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 过程展示档位（对标 DSH 的工作步骤展示）：只影响界面对「思考 / 工具」的显隐，
/// 永不隐藏最终答案。
/// </summary>
public enum ProcessDisplayMode
{
    /// <summary>只留答案 + 一行过程摘要；运行中也不展示逐行细节。</summary>
    Compact,

    /// <summary>完成轮次把过程收成一行组头，点开才看逐行；运行中展开。默认。</summary>
    Standard,

    /// <summary>过程的逐行始终可见（仍可手动收起）。</summary>
    Detailed,

    /// <summary>不分过程组，思考 / 工具 / 正文按真实顺序平铺。</summary>
    Expanded
}

/// <summary>过程组的收起时机。</summary>
public enum ProcessCollapseTiming
{
    /// <summary>本回合生成结束即收起（用户手点过的不动）。</summary>
    Completion,

    /// <summary>等下一条输入到达才收起，方便回看刚想过什么。</summary>
    NextInput
}

/// <summary>过程的切分方式。</summary>
public enum ProcessGrouping
{
    /// <summary>收成一行组头，点开看逐行。</summary>
    Collapsed,

    /// <summary>逐行可见，但仍有组头。</summary>
    History,

    /// <summary>不分组，按真实顺序平铺。</summary>
    None
}

/// <summary>
/// 一次渲染要用的过程展示策略：把档位摊平成四个可判断的开关，界面里不再散落 if。
/// 字段含义与 DSH 的 display policy 一致（foldCompletedTurns / stepGrouping /
/// liveProcessDetail / settledReasoningPreview / collapseTiming）。
/// </summary>
public sealed record ProcessDisplayPolicy(
    bool FoldCompletedTurns,
    ProcessGrouping Grouping,
    bool LiveProcessDetail,
    bool SettledReasoningPreview,
    ProcessCollapseTiming CollapseTiming)
{
    /// <summary>按档位取策略。</summary>
    public static ProcessDisplayPolicy For(ProcessDisplayMode mode) => mode switch
    {
        ProcessDisplayMode.Compact => new(true, ProcessGrouping.Collapsed, false, false, ProcessCollapseTiming.Completion),
        ProcessDisplayMode.Detailed => new(true, ProcessGrouping.History, true, true, ProcessCollapseTiming.Completion),
        ProcessDisplayMode.Expanded => new(false, ProcessGrouping.None, false, true, ProcessCollapseTiming.Completion),
        _ => new(true, ProcessGrouping.Collapsed, true, true, ProcessCollapseTiming.Completion)
    };

    /// <summary>按设置取策略（档位 + 独立的收起时机）。</summary>
    public static ProcessDisplayPolicy From(ChatPresentationSettings? settings)
    {
        var mode = settings?.ProcessMode ?? ProcessDisplayMode.Standard;
        return For(mode) with { CollapseTiming = settings?.CollapseTiming ?? ProcessCollapseTiming.Completion };
    }
}

/// <summary>对话呈现偏好（设置页 → 通用）。老 settings.json 没有该节点时走默认值。</summary>
public sealed class ChatPresentationSettings
{
    /// <summary>过程展示档位，默认「标准」。</summary>
    public ProcessDisplayMode ProcessMode { get; set; } = ProcessDisplayMode.Standard;

    /// <summary>过程组收起时机，默认「生成结束」。</summary>
    public ProcessCollapseTiming CollapseTiming { get; set; } = ProcessCollapseTiming.Completion;
}
