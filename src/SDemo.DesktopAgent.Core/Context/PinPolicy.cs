using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>一次 pin 切换的结果：是否改了状态、当前总数、给用户的提示。</summary>
public sealed record PinToggleResult(bool Changed, bool Pinned, int PinnedCount, string? Message);

/// <summary>
/// pin 的数量上限规则（P2 §3.4）。放在 Core 而不是组件里：
/// 上限判断需要单测，UI 只负责把结果渲染出来。
/// </summary>
public static class PinPolicy
{
    /// <summary>
    /// 切换一条消息的 pin。达到上限且要新 pin 时拒绝并给出提示；
    /// 取消 pin 永远允许。
    /// </summary>
    public static PinToggleResult Toggle(ChatSession session, ChatMessage message, int maxPinned)
    {
        var limit = Math.Max(0, maxPinned);
        var count = session.Messages.Count(item => item.IsPinned);

        if (!message.IsPinned && count >= limit)
        {
            return new PinToggleResult(
                Changed: false,
                Pinned: false,
                PinnedCount: count,
                Message: $"最多 pin {limit} 条消息，请先取消一条再试。");
        }

        message.IsPinned = !message.IsPinned;
        var next = message.IsPinned ? count + 1 : Math.Max(0, count - 1);

        return new PinToggleResult(
            Changed: true,
            Pinned: message.IsPinned,
            PinnedCount: next,
            Message: null);
    }

    /// <summary>当前会话里被 pin 的消息数。</summary>
    public static int Count(ChatSession session) => session.Messages.Count(message => message.IsPinned);
}
