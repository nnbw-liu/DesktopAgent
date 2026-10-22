using SDemo.DesktopAgent.Models;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Context;

/// <summary>渲染结果：完整正文 + 附件块 + 轨迹块，便于分段计量。</summary>
public sealed record RenderedContent(string Text, string? AttachmentBlock = null, string? TraceText = null);

/// <summary>把一条消息渲染成请求内容；<paramref name="attachmentCharLimit"/> 为 0 表示附件只留桩。</summary>
public delegate RenderedContent ContextMessageRenderer(
    ChatMessageModel message,
    int attachmentCharLimit,
    string? toolTraceText);

/// <summary>L1 裁剪参数。消息预算已扣掉固定块与省略提示的预留。</summary>
public sealed record TrimOptions(
    int MessageBudget,
    int TotalBudget,
    int KeepRecentTurns,
    ITokenCounter Counter,
    double CalibrationFactor,
    IReadOnlySet<string>? PinnedMessageIds = null,
    int MaxPinnedMessages = 20,
    double PinnedShareLimit = 0.30,
    int PinnedTruncateChars = 500,
    int ToolTraceTokenBudget = 300,
    int AttachmentInlineChars = 8192,
    int SkipBefore = 0)
{
    public static TrimOptions FromPolicy(
        ContextPolicy policy,
        int messageBudget,
        ITokenCounter counter,
        double calibrationFactor,
        IReadOnlySet<string>? pinnedMessageIds,
        int skipBefore = 0) => new(
        MessageBudget: messageBudget,
        TotalBudget: policy.Budget.Total,
        KeepRecentTurns: policy.KeepRecentTurns,
        Counter: counter,
        CalibrationFactor: calibrationFactor,
        PinnedMessageIds: pinnedMessageIds,
        MaxPinnedMessages: policy.MaxPinnedMessages,
        PinnedShareLimit: policy.PinnedShareLimit,
        PinnedTruncateChars: policy.PinnedTruncateChars,
        ToolTraceTokenBudget: policy.ToolTraceTokenBudget,
        AttachmentInlineChars: policy.AttachmentInlineChars,
        SkipBefore: skipBefore);
}

/// <summary>裁剪后的一条请求消息。</summary>
public sealed record TrimmedMessage(
    ChatMessageModel Source,
    RenderedContent Rendered,
    bool IsPinned,
    string? OmissionNote);

/// <summary>L1 裁剪结果与统计。</summary>
public sealed record TrimResult(
    IReadOnlyList<TrimmedMessage> Messages,
    TrimStats Stats)
{
    public bool OverBudget => Stats.TotalTokens > Stats.MessageBudget;
}

/// <summary>裁剪统计：报告与 UI 明细都从这里取值。</summary>
public sealed record TrimStats(
    int MessageBudget,
    int TotalBudget,
    int SystemTokens,
    int SkillTokens,
    int TurnTokens,
    int AttachmentTokens,
    int TraceTokens,
    int PinnedTokens,
    int OmittedMessages,
    int OmittedTurns,
    int OmittedTokens,
    int KeptTurns,
    int TotalTurns,
    int PinnedCount,
    bool PinnedTruncated,
    bool AttachmentsStubbed,
    bool Trimmed,
    int CompactedMessages = 0)
{
    public int MessageTokens => TurnTokens + AttachmentTokens + TraceTokens + PinnedTokens;

    /// <summary>含固定块的总估算（trim 时固定块已经单独计过）。</summary>
    public int TotalTokens => SystemTokens + SkillTokens + MessageTokens;
}

/// <summary>
/// L1 确定性裁剪（§3.5）：
/// 按轮分组、保留最近 N 轮、从最旧整轮淘汰、当前输入与附件永不淘汰、
/// 历史附件改桩、工具轨迹限额回放、pin 无条件保留但不允许反噬预算。
/// 这里不调用模型，也不改写任何领域消息。
/// </summary>
public sealed class ContextTrimmer
{
    /// <summary>省略提示的 token 预留（提示本身算在第一条保留消息里）。</summary>
    private const int OmissionNoteReserve = 40;

    public TrimResult Trim(
        IReadOnlyList<ChatMessageModel> messages,
        TrimOptions options,
        ContextMessageRenderer render)
    {
        if (messages.Count == 0)
        {
            return new TrimResult([], new TrimStats(
                options.MessageBudget, options.TotalBudget, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, false));
        }

        var turns = BuildTurns(messages);
        var totalTurns = turns.Count;
        var currentTurn = totalTurns - 1;
        var keepFrom = Math.Max(0, totalTurns - Math.Max(1, options.KeepRecentTurns));
        var skipBefore = Math.Clamp(options.SkipBefore, 0, messages.Count);

        var pinnedIds = ResolvePinnedIds(messages, options);
        var pinned = new bool[messages.Count];

        // 超过 pin 上限时只保留最近的 N 条，其余按普通消息参与淘汰。
        var pinnedCandidates = Enumerable.Range(0, messages.Count)
            .Where(index => pinnedIds.Contains(messages[index].Id))
            .ToList();

        if (pinnedCandidates.Count > options.MaxPinnedMessages)
        {
            pinnedCandidates = pinnedCandidates
                .Skip(pinnedCandidates.Count - options.MaxPinnedMessages)
                .ToList();
        }

        foreach (var index in pinnedCandidates)
        {
            pinned[index] = true;
        }

        // 已被摘要覆盖的消息不再发送：它们由系统块里的摘要代表（P2 §3.2）。
        // 例外：用户 pin 的消息始终保留原文（pin 的语义就是「别让模型忘掉这条」）。
        var keep = new bool[messages.Count];

        for (var index = 0; index < messages.Count; index++)
        {
            keep[index] = index >= skipBefore || pinned[index];
        }

        var evictableTurns = Enumerable.Range(0, keepFrom)
            .Where(index => turns[index].Start >= skipBefore)
            .ToList();
        var nextEvictable = 0;

        var attachmentChars = Math.Max(0, options.AttachmentInlineChars);
        var traceBudget = Math.Max(0, options.ToolTraceTokenBudget);

        var traceTexts = AllocateTraces(messages, keep, pinned, traceBudget, options.Counter);
        var rendered = RenderAll(messages, keep, pinned, attachmentChars, traceTexts, render, options.Counter, skipBefore);
        var rawTokens = rendered.Sum(item => item.Tokens);

        var budgetRaw = EffectiveRawBudget(options);

        // 反复收紧直到进入预算：先淘汰整轮，再缩附件，最后压轨迹。
        for (var guard = 0; guard < 500 && rawTokens > budgetRaw; guard++)
        {
            var evicted = false;

            while (nextEvictable < evictableTurns.Count)
            {
                var turn = turns[evictableTurns[nextEvictable++]];
                if (turn.Index == currentTurn)
                {
                    continue;
                }

                var anyEvicted = false;

                for (var index = turn.Start; index < turn.End; index++)
                {
                    if (pinned[index] || !keep[index])
                    {
                        continue;
                    }

                    keep[index] = false;
                    anyEvicted = true;
                }

                if (anyEvicted)
                {
                    evicted = true;
                    break;
                }
            }

            if (!evicted)
            {
                if (attachmentChars > 0)
                {
                    attachmentChars = attachmentChars >= 2048 ? attachmentChars / 2 : 0;
                }
                else if (traceBudget > 0)
                {
                    traceBudget = traceBudget >= 100 ? traceBudget / 2 : 0;
                    traceTexts = AllocateTraces(messages, keep, pinned, traceBudget, options.Counter);
                }
                else
                {
                    break;
                }
            }

            rendered = RenderAll(messages, keep, pinned, attachmentChars, traceTexts, render, options.Counter, skipBefore);
            rawTokens = rendered.Sum(item => item.Tokens);
        }

        // pin 内容超过总预算的 30%：截断到 N 字符，避免 pin 反噬预算（§3.5 规则 8）。
        var pinnedTokens = rendered.Where(item => item.IsPinned).Sum(item => item.Tokens);
        var pinnedLimit = (int)Math.Floor(options.TotalBudget * options.PinnedShareLimit);
        var pinnedTruncated = false;

        if (pinnedLimit > 0 && pinnedTokens > pinnedLimit)
        {
            pinnedTruncated = true;

            rendered = rendered
                .Select(item => item.IsPinned && item.Rendered.Text.Length > options.PinnedTruncateChars
                    ? item with
                    {
                        Rendered = item.Rendered with
                        {
                            Text = string.Concat(
                                item.Rendered.Text.AsSpan(0, Math.Max(0, options.PinnedTruncateChars)),
                                "…（pin 内容已截断）")
                        }
                    }
                    : item)
                .ToList();

            rawTokens = rendered.Sum(item => item.Tokens);
        }

        var compactedMessages = rendered.Count(item => item.Compacted);
        var omittedMessages = rendered.Count(item => !item.Kept && !item.Compacted);
        var omittedTokens = rendered.Where(item => !item.Kept && !item.Compacted).Sum(item => item.Tokens);
        var omittedTurns = turns.Count(turn => Enumerable.Range(turn.Start, turn.End - turn.Start)
            .Any(index => !rendered[index].Kept && !rendered[index].Compacted));

        var kept = rendered.Where(item => item.Kept).ToList();

        // 省略提示只放在第一条保留消息前；不新增 system 消息（§3.5 规则 5）。
        var omissionNote = omittedMessages > 0
            ? $"[上下文提示] {(compactedMessages > 0 ? $"更早的 {compactedMessages} 条历史已压缩为摘要；" : string.Empty)}"
              + $"已省略 {omittedMessages} 条更早消息（约 {FormatTokens(omittedTokens)} token）；需要时请重新读取文件或使用工具。"
            : null;

        var keptMessages = new List<TrimmedMessage>(kept.Count);
        for (var index = 0; index < kept.Count; index++)
        {
            keptMessages.Add(new TrimmedMessage(
                kept[index].Source,
                kept[index].Rendered,
                kept[index].IsPinned,
                index == 0 ? omissionNote : null));
        }

        var noteTokens = omissionNote is null
            ? 0
            : options.Counter.CountMessage("user", omissionNote);

        var stats = new TrimStats(
            options.MessageBudget,
            options.TotalBudget,
            SystemTokens: 0,
            SkillTokens: 0,
            // 省略提示确实附在第一条消息正文里，计入 TurnTokens，保证报告与实际发出的内容一致。
            TurnTokens: kept.Where(item => !item.IsPinned).Sum(item => item.TurnTokens) + noteTokens,
            AttachmentTokens: kept.Where(item => !item.IsPinned).Sum(item => item.AttachmentTokens),
            TraceTokens: kept.Where(item => !item.IsPinned).Sum(item => item.TraceTokens),
            PinnedTokens: kept.Where(item => item.IsPinned).Sum(item => item.Tokens),
            OmittedMessages: omittedMessages,
            OmittedTurns: omittedTurns,
            OmittedTokens: omittedTokens,
            KeptTurns: turns.Count(turn => Enumerable.Range(turn.Start, turn.End - turn.Start)
                .Any(index => rendered[index].Kept)),
            TotalTurns: totalTurns,
            PinnedCount: kept.Count(item => item.IsPinned),
            PinnedTruncated: pinnedTruncated,
            AttachmentsStubbed: rendered.Any(item => item.AttachmentsStubbed && !item.Compacted),
            Trimmed: omittedMessages > 0 || pinnedTruncated || attachmentChars < options.AttachmentInlineChars,
            CompactedMessages: compactedMessages);

        return new TrimResult(keptMessages, stats);
    }

    private static int EffectiveRawBudget(TrimOptions options)
    {
        var budget = Math.Max(0, options.MessageBudget - OmissionNoteReserve);
        var factor = options.CalibrationFactor <= 0 ? 1.0 : options.CalibrationFactor;

        // 预算以「校准后的 token」为单位，这里换算成原始估算可用的额度。
        return (int)Math.Floor(budget / factor);
    }

    private static Dictionary<string, string> AllocateTraces(
        IReadOnlyList<ChatMessageModel> messages,
        bool[] keep,
        bool[] pinned,
        int traceBudget,
        ITokenCounter counter)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (traceBudget <= 0)
        {
            return result;
        }

        var remaining = traceBudget;

        // 从最新往回分配：越近的轨迹越重要。
        for (var index = messages.Count - 1; index >= 0 && remaining > 0; index--)
        {
            if (!keep[index] || pinned[index])
            {
                continue;
            }

            var trace = messages[index].ToolTrace;
            if (trace.Count == 0)
            {
                continue;
            }

            var render = ToolTraceFormatter.Render(trace, remaining, counter);
            if (render is null)
            {
                continue;
            }

            result[messages[index].Id] = render.Text;
            remaining -= render.Tokens;
        }

        return result;
    }

    private static List<RenderedItem> RenderAll(
        IReadOnlyList<ChatMessageModel> messages,
        bool[] keep,
        bool[] pinned,
        int attachmentChars,
        IReadOnlyDictionary<string, string> traceTexts,
        ContextMessageRenderer render,
        ITokenCounter counter,
        int skipBefore)
    {
        var items = new List<RenderedItem>(messages.Count);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var traceText = traceTexts.TryGetValue(message.Id, out var text) ? text : null;

            // 只有「当前轮」的附件才内联；历史轮次一律只留桩。
            var inline = keep[index] && attachmentChars > 0 && IsInCurrentTurn(messages, index);
            var content = render(message, inline ? attachmentChars : 0, traceText);

            var total = counter.CountMessage(RoleText(message), content.Text);
            var attachment = content.AttachmentBlock is null
                ? 0
                : counter.CountMessage(RoleText(message), content.AttachmentBlock);
            var trace = content.TraceText is null
                ? 0
                : counter.CountMessage(RoleText(message), content.TraceText);

            items.Add(new RenderedItem(
                message,
                content,
                keep[index],
                pinned[index],
                total,
                Math.Max(0, attachment),
                Math.Max(0, trace),
                Math.Max(0, total - attachment - trace),
                // 历史消息带附件、或附件块被内联上限压掉时，都记一次「已改桩」。
                AttachmentsStubbed: message.Attachments.Count > 0 && (!inline || content.AttachmentBlock is null),
                Compacted: index < skipBefore && !pinned[index]));
        }

        return items;
    }

    private static bool IsInCurrentTurn(IReadOnlyList<ChatMessageModel> messages, int index)
    {
        for (var cursor = messages.Count - 1; cursor >= 0; cursor--)
        {
            if (messages[cursor].Role == ChatMessageRole.User)
            {
                return index >= cursor;
            }
        }

        // 没有任何 user 消息（例如只有系统 / 助手消息）：历史一律视为可淘汰的旧内容。
        return false;
    }

    private static HashSet<string> ResolvePinnedIds(
        IReadOnlyList<ChatMessageModel> messages,
        TrimOptions options)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (message.IsPinned)
            {
                ids.Add(message.Id);
            }
        }

        if (options.PinnedMessageIds is { Count: > 0 } explicitIds)
        {
            ids.UnionWith(explicitIds);
        }

        return ids;
    }

    private static List<TurnRange> BuildTurns(IReadOnlyList<ChatMessageModel> messages)
    {
        var turns = new List<TurnRange>();
        var start = 0;

        for (var index = 0; index < messages.Count; index++)
        {
            if (index > start && messages[index].Role == ChatMessageRole.User)
            {
                turns.Add(new TurnRange(turns.Count, start, index));
                start = index;
            }
        }

        turns.Add(new TurnRange(turns.Count, start, messages.Count));
        return turns;
    }

    private static string RoleText(ChatMessageModel message) =>
        message.Role == ChatMessageRole.User ? "user" : "assistant";

    private static string FormatTokens(int tokens) => tokens >= 1024
        ? $"{tokens / 1024.0:F1}K"
        : tokens.ToString();

    private sealed record TurnRange(int Index, int Start, int End);

    private sealed record RenderedItem(
        ChatMessageModel Source,
        RenderedContent Rendered,
        bool Kept,
        bool IsPinned,
        int Tokens,
        int AttachmentTokens,
        int TraceTokens,
        int TurnTokens,
        bool AttachmentsStubbed,
        bool Compacted);
}
