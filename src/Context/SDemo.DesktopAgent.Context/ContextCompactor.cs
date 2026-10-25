using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>一次压缩请求：会话 + 会话模型 + 预算。摘要模型由调用方先解析好再传进来。</summary>
/// <param name="Instructions">
/// 本次请求的系统提示词（P2b §3.3.2 第二条）。摘要请求用它当 system，而不是换成摘要器自己的提示词，
/// 这样摘要调用是上一次请求的真前缀。为 null 时退回摘要器的固定提示词。
/// </param>
/// <param name="Tools">本次请求声明的工具（同一份热前缀），摘要只声明、不执行。</param>
public sealed record ContextCompactionRequest(
    ChatSession Session,
    ModelProvider Provider,
    string ModelId,
    ModelProfile Profile,
    ContextSettings Settings,
    ContextBudget Budget,
    bool Force = false,
    string? Instructions = null,
    IReadOnlyList<AITool>? Tools = null);

/// <summary>压缩结果；<see cref="Compacted"/> 为 false 时 <see cref="Message"/> 说明原因。</summary>
/// <param name="Rejected">摘要没有缩小上下文，被拒绝（原文与旧摘要都不动）。</param>
/// <param name="Busy">上一次压缩还没收尾（活着的压缩者），本次让路。</param>
public sealed record CompactionOutcome(
    bool Compacted,
    string Message,
    int CompactedMessages = 0,
    int SummaryTokens = 0,
    int ThroughOrder = 0,
    bool UsedFallback = false,
    bool Failed = false,
    string? Error = null,
    int SummaryInputTokens = 0,
    int SummaryOutputTokens = 0,
    bool Rejected = false,
    bool Busy = false)
{
    public static CompactionOutcome NotNeeded(string message) => new(false, message);

    public static CompactionOutcome Failure(string message, string? error = null) =>
        new(false, message, Failed: true, Error: error);

    public static CompactionOutcome Reject(string message) =>
        new(false, message, Rejected: true);

    public static CompactionOutcome InProgress(string message) =>
        new(false, message, Busy: true);
}

/// <summary>
/// 增量压缩编排（P2 §3.1 + P2b §3.3.2）：
/// <code>
/// chunkStart = SummaryThroughOrder
/// keepFrom   = 最后 KeepRecentTurns 轮的起点（且不越过 pin / 快照 / 最后一条消息）
/// chunk      = messages[chunkStart .. keepFrom]
/// 开始标记（事务 + 锁）→ LLM 摘要（失败退兜底）→ 不缩小则拒绝 → 一次性提交摘要与覆盖范围
/// </code>
/// <para>
/// P2b 补的三个性质：
/// <list type="number">
/// <item><b>先修剪，后摘要</b>：由 <see cref="ContextTrimmer"/> 在报「需要淘汰历史」之前先把工具轨迹修到放得下，
/// 所以「到这里要摘要」本身就意味着修剪已经不够用了（见 <see cref="ContextPruner"/>）。</item>
/// <item><b>摘要请求复用热前缀</b>：<paramref name="ContextCompactionRequest.Instructions"/> +
/// <paramref name="ContextCompactionRequest.Tools"/> + 被遮蔽区域逐条回放，最后追加压缩指令。</item>
/// <item><b>事务化 + 拒绝不缩小</b>：开始标记先落库（未配对 = 有压缩没跑完，崩溃后是证据而不是半截状态）；
/// 摘要不比被替换的内容小时直接拒绝，原文与旧摘要都不动。</item>
/// </list>
/// </para>
/// 只改会话的摘要字段与压缩标记，不动一条原文；保存失败会回滚这一次的内存修改。
/// </summary>
public sealed class ContextCompactor
{
    /// <summary>压缩标记里记录的来源：自动 / 手动 / 拒绝 / 失败。</summary>
    public const string ReasonAuto = "auto";
    public const string ReasonManual = "manual";
    public const string ReasonRejected = "rejected";

    private readonly ITokenCounter _counter;
    private readonly IContextSummarizer? _summarizer;
    private readonly ExtractiveContextSummarizer _fallback;
    private readonly Func<ChatSession, CancellationToken, Task>? _persist;
    private readonly TimeProvider _time;

    public ContextCompactor(
        ITokenCounter counter,
        IContextSummarizer? summarizer,
        ExtractiveContextSummarizer fallback,
        Func<ChatSession, CancellationToken, Task>? persist = null,
        TimeProvider? time = null)
    {
        _counter = counter;
        _summarizer = summarizer;
        _fallback = fallback;
        _persist = persist;
        _time = time ?? TimeProvider.System;
    }

    public async Task<CompactionOutcome> EnsureAsync(
        ContextCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = request.Session;
        var messages = session.Messages;

        // 事务（P2b §3.3.2 第三条）：开始标记没有收尾 = 上一次压缩还在跑。
        // 同一个进程里那就是「活着的压缩者」，本次让路；从数据库读回时（InterruptedOnLoad）
        // 说明上一个进程已经不在，这时由本次压缩接管，并把它当作「上次没跑完，重做这一段」。
        if (session.HasUnfinishedCompaction && !session.CompactionInterruptedOnLoad)
        {
            return CompactionOutcome.InProgress(
                $"上一次压缩还没收尾（{FormatTime(session.CompactionStartedAt)} 开始），本次先跳过。");
        }

        var recovered = session.CompactionInterruptedOnLoad && session.HasUnfinishedCompaction;

        if (messages.Count < 2)
        {
            return CompactionOutcome.NotNeeded("消息太少，暂时不需要压缩。");
        }

        var keepFrom = ResolveKeepFrom(request, messages);

        if (keepFrom <= 0)
        {
            return CompactionOutcome.NotNeeded("最近的轮次已覆盖全部历史，暂时不需要压缩。");
        }

        var chunkStart = Math.Clamp(session.SummaryThroughOrder, 0, messages.Count);

        // pin 的消息必须一直可见：摘要范围不能越过最早一条 pin。
        var pinnedFloor = FindPinnedFloor(messages, chunkStart, keepFrom);
        if (pinnedFloor is int floor && floor < keepFrom)
        {
            keepFrom = floor;
        }

        if (keepFrom <= chunkStart)
        {
            return CompactionOutcome.NotNeeded(
                session.SummaryThroughOrder > 0
                    ? "没有新的历史需要压缩（摘要已经覆盖到最新需要压缩的位置）。"
                    : "没有可压缩的历史（pin 或最近轮次覆盖了全部消息）。");
        }

        var chunk = messages.GetRange(chunkStart, keepFrom - chunkStart);
        var chunkTokens = EstimateTokens(chunk);
        var chunkTurns = chunk.Count(message => message.Role == ChatMessageRole.User);

        // 避免每轮都摘要：新增不足 4 轮且省不下 200 token 时不动。
        if (!request.Force
            && chunkTurns < 4
            && chunkTokens - Math.Max(0, request.Settings.SummaryMaxTokens) < 200)
        {
            return CompactionOutcome.NotNeeded($"新增历史只有 {chunkTurns} 轮，暂不压缩。");
        }

        var summaryMaxTokens = Math.Clamp(request.Settings.SummaryMaxTokens, 128, 4096);
        var inputMaxTokens = Math.Clamp(request.Settings.SummaryInputMaxTokens, 512, 24_000);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.Settings.SummaryTimeoutSeconds, 1, 300));

        // 事务开始：先把开始标记落库（它同时是锁）。库里留下「只有开始」的状态时，
        // 下一次打开会话就能看到「上次压缩未完成」，而不是会话莫名变短。
        var previous = CompactionState.Capture(session);
        var startedAt = _time.GetLocalNow().DateTime;
        session.CompactionStartedAt = startedAt;
        session.CompactionFinishedAt = null;
        session.CompactionReason = request.Force ? ReasonManual : ReasonAuto;
        session.CompactionInterruptedOnLoad = false;

        if (await TryPersistAsync(session, cancellationToken).ConfigureAwait(false) is { } startError)
        {
            Restore(session, previous);
            return CompactionOutcome.Failure($"压缩开始标记保存失败，本轮不做摘要：{startError.Message}", startError.Message);
        }

        var (provider, modelId) = (request.Provider, request.ModelId);
        var summary = session.Summary ?? string.Empty;
        var usedFallback = false;
        string? lastError = null;
        var summaryInputTokens = 0;
        var summaryOutputTokens = 0;

        try
        {
            foreach (var (batch, batchStart) in SplitByTokens(chunk, chunkStart, inputMaxTokens))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var summaryRequest = new SummaryRequest(
                    string.IsNullOrWhiteSpace(summary) ? null : summary,
                    batch,
                    batchStart,
                    provider,
                    modelId,
                    summaryMaxTokens,
                    inputMaxTokens,
                    timeout,
                    Instructions: request.Instructions,
                    Tools: request.Tools);

                var result = _summarizer is null
                    ? SummaryResult.Failed("未配置摘要模型")
                    : await _summarizer.SummarizeAsync(summaryRequest, cancellationToken).ConfigureAwait(false);

                // R1.4：模型摘要的真实成本按批累计（兜底不调用模型，成本为 0）。
                summaryInputTokens += result.InputTokens;
                summaryOutputTokens += result.OutputTokens;

                if (result.Ok)
                {
                    summary = result.Text;
                    continue;
                }

                lastError = result.Error;
                usedFallback = true;

                var fallback = await _fallback.SummarizeAsync(summaryRequest, cancellationToken)
                    .ConfigureAwait(false);
                summary = fallback.Text;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消：摘要没提交，库里留下「只有开始」的标记（下次打开会显示未完成，并重做这一段）。
            // 内存里仍然要把事务收尾，否则这个会话在本进程里会永远停在「有压缩在跑」。
            await AbortAsync(session, previous, startedAt, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // 摘要器理论上只返回失败，但兜底也是普通代码：异常也要把事务收尾，否则这个会话
            // 会永远停在「有压缩在跑」，之后每一轮都让路。
            await AbortAsync(session, previous, startedAt, $"failed:{ex.GetType().Name}")
                .ConfigureAwait(false);
            throw;
        }

        summary = SummaryFormat.Cap(summary, summaryMaxTokens, _counter);

        if (string.IsNullOrWhiteSpace(summary))
        {
            await AbortAsync(session, previous, startedAt, "failed:empty").ConfigureAwait(false);
            return CompactionOutcome.Failure("摘要结果为空，已回退到确定性裁剪。", lastError);
        }

        var summaryTokens = _counter.Count(summary);

        // 不缩小的摘要直接拒绝（P2b §3.3.2 第三条，照 DSH：摘要必须比它替换掉的内容小）。
        // 被替换的是「这一段原文 + 上一版摘要」，因为摘要整体取代旧摘要。
        var replacedTokens = chunkTokens + (session.HasSummary ? session.SummaryTokens : 0);

        if (summaryTokens >= replacedTokens)
        {
            await AbortAsync(session, previous, startedAt, ReasonRejected).ConfigureAwait(false);

            return CompactionOutcome.Reject(
                $"摘要没有缩小上下文（{FormatTokens(replacedTokens)} → {FormatTokens(summaryTokens)} token），"
                + "已拒绝并保留原文。");
        }

        var snapshot = CompactionState.Capture(session);
        var finishedAt = _time.GetLocalNow().DateTime;

        session.Summary = summary;
        session.SummaryThroughOrder = keepFrom;
        session.SummaryUpdatedAt = finishedAt;
        session.SummaryTokens = summaryTokens;
        session.SummaryIsFallback = usedFallback || SummaryFormat.IsFallback(summary);
        session.CompactionFinishedAt = finishedAt;
        session.CompactionReason = session.SummaryIsFallback ? "auto-fallback" : session.CompactionReason;

        try
        {
            if (_persist is not null)
            {
                await _persist(session, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await AbortAsync(session, snapshot, startedAt, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 保存失败：这一轮不启用摘要，原样退回 L1（内存状态必须与库一致）。
            // 库里留下的是「只有开始」的标记，下次打开会话会显示「上次压缩未完成」。
            await AbortAsync(session, snapshot, startedAt, "failed:save").ConfigureAwait(false);
            return CompactionOutcome.Failure($"摘要保存失败，已回退到确定性裁剪：{ex.Message}", ex.Message);
        }

        var compacted = keepFrom - chunkStart;
        var suffix = usedFallback ? "（自动摘要）" : string.Empty;
        var recoveredNote = recovered ? "（上次压缩未完成，本次已重做）" : string.Empty;

        return new CompactionOutcome(
            Compacted: true,
            Message: $"已压缩 {compacted} 条历史（约 {FormatTokens(chunkTokens)} token）{suffix}{recoveredNote}",
            CompactedMessages: compacted,
            SummaryTokens: summaryTokens,
            ThroughOrder: keepFrom,
            UsedFallback: session.SummaryIsFallback,
            Error: lastError,
            SummaryInputTokens: summaryInputTokens,
            SummaryOutputTokens: summaryOutputTokens);
    }

    /// <summary>
    /// 计算本轮摘要覆盖的终点（不含）：最后 KeepRecentTurns 轮的起点，
    /// 且永远不越过 pin、宿主上下文快照与最后一条消息。
    /// </summary>
    private static int ResolveKeepFrom(ContextCompactionRequest request, IReadOnlyList<ChatMessageModel> messages)
    {
        var keepTurns = request.Profile.IsSmallModel
            ? request.Settings.SmallModelKeepRecentTurns
            : request.Settings.KeepRecentTurns;

        keepTurns = Math.Clamp(keepTurns, 1, 50);

        var turns = BuildTurnStarts(messages);
        var keepFrom = turns.Count > keepTurns
            ? turns[turns.Count - keepTurns]
            : 0;

        // 当前输入永远留在摘要之外（P1 的「当前输入永不淘汰」规则）。
        keepFrom = Math.Min(keepFrom, messages.Count - 1);

        // P2b：宿主上下文快照显式打「永不压缩」——它在结构上属于末尾的运行时事实，
        // 被摘要吃掉等于模型这一轮不知道当前策略 / 时间（P2a 把它追加在裁剪之后，这里是同一回事的显式表达）。
        var snapshotFloor = FindSnapshotFloor(messages, Math.Clamp(request.Session.SummaryThroughOrder, 0, messages.Count));
        return Math.Min(keepFrom, snapshotFloor);
    }

    /// <summary>第一条快照消息的下标；没有则返回 <see cref="int.MaxValue"/>（不设上界）。</summary>
    private static int FindSnapshotFloor(IReadOnlyList<ChatMessageModel> messages, int from)
    {
        for (var index = from; index < messages.Count; index++)
        {
            if (HostSnapshot.IsSnapshot(messages[index].Content))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static int? FindPinnedFloor(
        IReadOnlyList<ChatMessageModel> messages,
        int chunkStart,
        int keepFrom)
    {
        for (var index = chunkStart; index < keepFrom; index++)
        {
            if (messages[index].IsPinned)
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// 按 user 消息切轮，返回每轮起始下标。宿主上下文快照也是 <c>user</c> 消息，但它不是一轮对话
    /// （否则「最后 N 轮」会把它算成一轮，压缩范围平白少一轮）。
    /// </summary>
    private static List<int> BuildTurnStarts(IReadOnlyList<ChatMessageModel> messages)
    {
        var starts = new List<int> { 0 };

        for (var index = 1; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatMessageRole.User
                && !HostSnapshot.IsSnapshot(messages[index].Content))
            {
                starts.Add(index);
            }
        }

        return starts;
    }

    /// <summary>按 token 上限把 chunk 切成批；单条超限的消息独占一批。</summary>
    private List<(IReadOnlyList<ChatMessageModel> Batch, int Start)> SplitByTokens(
        IReadOnlyList<ChatMessageModel> chunk,
        int chunkStart,
        int maxTokens)
    {
        var batches = new List<(IReadOnlyList<ChatMessageModel>, int)>();
        var current = new List<ChatMessageModel>();
        var currentTokens = 0;
        var currentStart = chunkStart;

        for (var index = 0; index < chunk.Count; index++)
        {
            var message = chunk[index];
            var cost = EstimateMessageTokens(message, _counter);

            if (current.Count > 0 && currentTokens + cost > maxTokens)
            {
                batches.Add((current, currentStart));
                currentStart = chunkStart + index;
                current = [];
                currentTokens = 0;
            }

            current.Add(message);
            currentTokens += cost;
        }

        if (current.Count > 0)
        {
            batches.Add((current, currentStart));
        }

        return batches;
    }

    private int EstimateTokens(IReadOnlyList<ChatMessageModel> messages)
    {
        var total = 0;

        foreach (var message in messages)
        {
            total += EstimateMessageTokens(message, _counter);
        }

        return total;
    }

    private static int EstimateMessageTokens(ChatMessageModel message, ITokenCounter counter)
    {
        var role = message.Role == ChatMessageRole.User ? "user" : "assistant";
        var tokens = counter.CountMessage(role, message.Content ?? string.Empty);

        foreach (var trace in message.ToolTrace)
        {
            tokens += counter.CountMessage(role, ToolTraceFormatter.RenderLine(trace));
        }

        return tokens;
    }

    /// <summary>尽力落库：压缩是增强能力，写库失败不该把异常抛进聊天主流程。</summary>
    private async Task<Exception?> TryPersistAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (_persist is null)
        {
            return null;
        }

        try
        {
            await _persist(session, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// 失败 / 取消收尾：把摘要字段退回原样，事务标记写成「已结束 + 原因」。
    /// <para>
    /// 落库用 <see cref="CancellationToken.None"/>：用户取消正是最常见的收尾场景，用已取消的令牌写库
    /// 只会把这个会话留在「有压缩在跑」——而内存里的标记必须能继续用。写不进去也不抛：
    /// 库里那份「只有开始」会在下次打开时被识别成「上次压缩未完成」。
    /// </para>
    /// </summary>
    private async Task AbortAsync(
        ChatSession session,
        CompactionState state,
        DateTime startedAt,
        string reason)
    {
        Restore(session, state);
        session.CompactionStartedAt = startedAt;
        session.CompactionFinishedAt = _time.GetLocalNow().DateTime;
        session.CompactionReason = reason;
        session.CompactionInterruptedOnLoad = false;
        await TryPersistAsync(session, CancellationToken.None).ConfigureAwait(false);
    }

    private static void Restore(ChatSession session, CompactionState state)
    {
        session.Summary = state.Summary;
        session.SummaryThroughOrder = state.ThroughOrder;
        session.SummaryUpdatedAt = state.UpdatedAt;
        session.SummaryTokens = state.Tokens;
        session.SummaryIsFallback = state.Fallback;
        session.CompactionStartedAt = state.StartedAt;
        session.CompactionFinishedAt = state.FinishedAt;
        session.CompactionReason = state.Reason;
    }

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();

    private static string FormatTime(DateTime? time) => time?.ToString("HH:mm:ss") ?? "刚刚";

    /// <summary>压缩事务的完整快照：摘要字段 + 三个标记字段（回滚必须一起回）。</summary>
    private sealed record CompactionState(
        string? Summary,
        int ThroughOrder,
        DateTime? UpdatedAt,
        int Tokens,
        bool Fallback,
        DateTime? StartedAt,
        DateTime? FinishedAt,
        string? Reason)
    {
        public static CompactionState Capture(ChatSession session) => new(
            session.Summary,
            session.SummaryThroughOrder,
            session.SummaryUpdatedAt,
            session.SummaryTokens,
            session.SummaryIsFallback,
            session.CompactionStartedAt,
            session.CompactionFinishedAt,
            session.CompactionReason);
    }
}
