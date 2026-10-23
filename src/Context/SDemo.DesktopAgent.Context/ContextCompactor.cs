using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>一次压缩请求：会话 + 会话模型 + 预算。摘要模型由调用方先解析好再传进来。</summary>
public sealed record ContextCompactionRequest(
    ChatSession Session,
    ModelProvider Provider,
    string ModelId,
    ModelProfile Profile,
    ContextSettings Settings,
    ContextBudget Budget,
    bool Force = false);

/// <summary>压缩结果；<see cref="Compacted"/> 为 false 时 <see cref="Message"/> 说明原因。</summary>
public sealed record CompactionOutcome(
    bool Compacted,
    string Message,
    int CompactedMessages = 0,
    int SummaryTokens = 0,
    int ThroughOrder = 0,
    bool UsedFallback = false,
    bool Failed = false,
    string? Error = null)
{
    public static CompactionOutcome NotNeeded(string message) => new(false, message);

    public static CompactionOutcome Failure(string message, string? error = null) =>
        new(false, message, Failed: true, Error: error);
}

/// <summary>
/// 增量压缩编排（P2 §3.1）：
/// <code>
/// chunkStart = SummaryThroughOrder
/// keepFrom   = 最后 KeepRecentTurns 轮的起点（且不越过 pin / 最后一条消息）
/// chunk      = messages[chunkStart .. keepFrom]
/// LLM 摘要失败 → 确定性兜底 → Cap → 原子保存
/// </code>
/// 只改会话的摘要字段，不动一条原文；保存失败会回滚这一次的内存修改。
/// </summary>
public sealed class ContextCompactor
{
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

        if (messages.Count < 2)
        {
            return CompactionOutcome.NotNeeded("消息太少，暂时不需要压缩。");
        }

        var keepFrom = ResolveKeepFrom(request);

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

        var (provider, modelId) = (request.Provider, request.ModelId);
        var summary = session.Summary ?? string.Empty;
        var usedFallback = false;
        string? lastError = null;

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
                timeout);

            var result = _summarizer is null
                ? SummaryResult.Failed("未配置摘要模型")
                : await _summarizer.SummarizeAsync(summaryRequest, cancellationToken).ConfigureAwait(false);

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

        summary = SummaryFormat.Cap(summary, summaryMaxTokens, _counter);

        if (string.IsNullOrWhiteSpace(summary))
        {
            return CompactionOutcome.Failure("摘要结果为空，已回退到确定性裁剪。", lastError);
        }

        var snapshot = new SummarySnapshot(
            session.Summary,
            session.SummaryThroughOrder,
            session.SummaryUpdatedAt,
            session.SummaryTokens,
            session.SummaryIsFallback);

        var summaryTokens = _counter.Count(summary);
        session.Summary = summary;
        session.SummaryThroughOrder = keepFrom;
        session.SummaryUpdatedAt = _time.GetLocalNow().DateTime;
        session.SummaryTokens = summaryTokens;
        session.SummaryIsFallback = usedFallback || SummaryFormat.IsFallback(summary);

        try
        {
            if (_persist is not null)
            {
                await _persist(session, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Restore(session, snapshot);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 保存失败：这一轮不启用摘要，原样退回 L1（内存状态必须与库一致）。
            Restore(session, snapshot);
            return CompactionOutcome.Failure($"摘要保存失败，已回退到确定性裁剪：{ex.Message}", ex.Message);
        }

        var compacted = keepFrom - chunkStart;
        var suffix = usedFallback ? "（自动摘要）" : string.Empty;

        return new CompactionOutcome(
            Compacted: true,
            Message: $"已压缩 {compacted} 条历史（约 {FormatTokens(chunkTokens)} token）{suffix}",
            CompactedMessages: compacted,
            SummaryTokens: summaryTokens,
            ThroughOrder: keepFrom,
            UsedFallback: session.SummaryIsFallback,
            Error: lastError);
    }

    /// <summary>
    /// 计算本轮摘要覆盖的终点（不含）：最后 KeepRecentTurns 轮的起点，
    /// 且永远不越过最后一条消息。
    /// </summary>
    private static int ResolveKeepFrom(ContextCompactionRequest request)
    {
        var messages = request.Session.Messages;
        var keepTurns = request.Profile.IsSmallModel
            ? request.Settings.SmallModelKeepRecentTurns
            : request.Settings.KeepRecentTurns;

        keepTurns = Math.Clamp(keepTurns, 1, 50);

        var turns = BuildTurnStarts(messages);
        var keepFrom = turns.Count > keepTurns
            ? turns[turns.Count - keepTurns]
            : 0;

        // 当前输入永远留在摘要之外（P1 的「当前输入永不淘汰」规则）。
        return Math.Min(keepFrom, messages.Count - 1);
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

    /// <summary>按 user 消息切轮，返回每轮起始下标。</summary>
    private static List<int> BuildTurnStarts(IReadOnlyList<ChatMessageModel> messages)
    {
        var starts = new List<int> { 0 };

        for (var index = 1; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatMessageRole.User)
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

    private static void Restore(ChatSession session, SummarySnapshot snapshot)
    {
        session.Summary = snapshot.Summary;
        session.SummaryThroughOrder = snapshot.ThroughOrder;
        session.SummaryUpdatedAt = snapshot.UpdatedAt;
        session.SummaryTokens = snapshot.Tokens;
        session.SummaryIsFallback = snapshot.Fallback;
    }

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();

    private sealed record SummarySnapshot(
        string? Summary,
        int ThroughOrder,
        DateTime? UpdatedAt,
        int Tokens,
        bool Fallback);
}
