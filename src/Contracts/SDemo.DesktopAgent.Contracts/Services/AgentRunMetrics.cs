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
    private readonly List<MemoryCitation> _citations = [];
    private int _rejectedToolCalls;
    private int _failedToolCalls;
    private int _approvalCount;

    /// <summary>本次生成（turn）的 id；checkpoint 按它聚合一次生成里的多次编辑。</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>本轮工具循环保护（P5）。</summary>
    public Context.ToolCallGuard Guard { get; } = new();

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public IReadOnlyDictionary<string, int> ToolCalls => _toolCalls;

    /// <summary>被宿主拒绝的调用数（循环保护 / 用户拒绝 / 连续失败禁用）。</summary>
    public int RejectedToolCalls => _rejectedToolCalls;

    /// <summary>执行失败的调用数（参数错误 / 工具异常 / 业务失败）。</summary>
    public int FailedToolCalls => _failedToolCalls;

    /// <summary>
    /// 本轮真正弹给用户确认的审批次数（R1.4）。评测里的自动审批不经过审批服务，
    /// 因此不会计入——这个数字只代表产品侧的人工确认成本。
    /// </summary>
    public int ApprovalCount => _approvalCount;

    /// <summary>记录一次已展示给用户的审批（由审批服务在请求真正上屏时调用）。</summary>
    public void RecordApproval() => _approvalCount++;

    /// <summary>一次生成最多保留的轨迹条数；只影响持久化 / 编辑摘要，不影响模型可见的最近 20 条。</summary>
    public const int MaxStoredEntries = 100;

    /// <summary>本次生成的工具轨迹（最多 <see cref="MaxStoredEntries"/> 条）。</summary>
    public IReadOnlyList<ToolTraceEntry> ToolTrace => _toolTrace;

    /// <summary>一次生成最多保留的引用条数。</summary>
    public const int MaxCitations = 10;

    /// <summary>M3：本轮命中的归档会话引用（按出现顺序去重）。</summary>
    public IReadOnlyList<MemoryCitation> Citations => _citations;

    /// <summary>M4：本轮采用的能力路由（暴露集合 / 上限 / 来源）。插件等未路由的路径为 null。</summary>
    public Context.RoutingDecision? Routing { get; private set; }

    /// <summary>记录本轮路由；实际调用统计仍由 <see cref="RecordToolCall"/> 记录。</summary>
    public void RecordRouting(Context.RoutingDecision decision) => Routing = decision;

    /// <summary>本轮暴露给模型的工具数（没有路由时为 0）。</summary>
    public int ExposedToolCount => Routing?.Count ?? 0;

    /// <summary>本轮实际调用过的工具数（去重）。</summary>
    public int CalledToolCount => _toolCalls.Count;

    /// <summary>暴露了但一次都没调用的工具（用于评估路由是否过度暴露）。</summary>
    public IReadOnlyList<string> UnusedExposedTools =>
        Routing is null
            ? []
            : Routing.ExposedTools
                .Where(name => !_toolCalls.ContainsKey(name))
                .ToArray();

    /// <summary>
    /// 实际调用了、但不在本轮暴露集合里的工具。正常应为空；出现时说明有路径绕过了路由，
    /// 保留成显式指标而不是静默忽略。
    /// </summary>
    public IReadOnlyList<string> CalledUnexposedTools =>
        Routing is null
            ? []
            : _toolCalls.Keys
                .Where(name => !Routing.Exposes(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

    /// <summary>记录一条知识库引用；同一会话只保留第一次出现。</summary>
    public void RecordCitation(MemoryCitation citation)
    {
        if (string.IsNullOrWhiteSpace(citation.SessionId)
            || _citations.Count >= MaxCitations
            || _citations.Any(item => string.Equals(item.SessionId, citation.SessionId, StringComparison.Ordinal)))
        {
            return;
        }

        _citations.Add(citation);
    }

    public void RecordToolCall(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _toolCalls[name] = _toolCalls.TryGetValue(name, out var count) ? count + 1 : 1;
    }

    /// <summary>
    /// 记录一次工具结果的状态。工具文本会同时带上 <c>[TOOL_STATUS:*]</c> 标记，
    /// 这里只做本轮计数，供评测和“被拒绝的写入不算成功”这类判断使用。
    /// </summary>
    public void RecordToolResult(string? name, string? status)
    {
        if (string.Equals(status, ToolTraceEntry.StatusRejected, StringComparison.Ordinal))
        {
            _rejectedToolCalls++;
        }
        else if (string.Equals(status, ToolTraceEntry.StatusError, StringComparison.Ordinal))
        {
            _failedToolCalls++;
        }
    }

    /// <summary>工具开始执行时先放一条 running 轨迹，UI 不用等结果返回。</summary>
    public void BeginToolTrace(ToolTraceEntry entry)
    {
        if (_toolTrace.Count >= MaxStoredEntries)
        {
            return;
        }

        _toolTrace.Add(entry);
    }

    /// <summary>
    /// 追加 / 完成一条轨迹：同 CallId 的 running 条目原地替换，避免开始与结束各占一行；
    /// 超过上限的静默丢弃（只保留最近的部分，前面已有汇总）。
    /// </summary>
    public void RecordToolTrace(ToolTraceEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.CallId))
        {
            var runningIndex = _toolTrace.FindLastIndex(item =>
                string.Equals(item.CallId, entry.CallId, StringComparison.Ordinal)
                && string.Equals(item.Status, ToolTraceEntry.StatusRunning, StringComparison.Ordinal));

            if (runningIndex >= 0)
            {
                _toolTrace[runningIndex] = entry;
                return;
            }
        }

        if (_toolTrace.Count >= MaxStoredEntries)
        {
            return;
        }

        _toolTrace.Add(entry);
    }

    /// <summary>流被取消 / 中断时，把还没拿到结果的 running 轨迹收成已取消。</summary>
    public void CancelRunningTools()
    {
        for (var index = 0; index < _toolTrace.Count; index++)
        {
            if (string.Equals(_toolTrace[index].Status, ToolTraceEntry.StatusRunning, StringComparison.Ordinal))
            {
                _toolTrace[index] = _toolTrace[index] with
                {
                    Status = ToolTraceEntry.StatusCanceled,
                    Summary = "已取消"
                };
            }
        }
    }

    public void AddUsage(int inputTokens, int outputTokens)
    {
        InputTokens += inputTokens;
        OutputTokens += outputTokens;
    }
}
