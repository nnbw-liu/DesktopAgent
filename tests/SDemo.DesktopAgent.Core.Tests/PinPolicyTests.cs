using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 pin 交互规则：上限、计数与取消。</summary>
public sealed class PinPolicyTests
{
    private static ChatSession SessionWith(int messages) => new()
    {
        Id = "s",
        Title = "pin",
        Messages = Enumerable.Range(0, messages)
            .Select(index => new ChatMessage(ChatMessageRole.User, $"消息 {index}"))
            .ToList()
    };

    [Fact]
    public void pin与取消pin正确计数()
    {
        var session = SessionWith(3);

        var first = PinPolicy.Toggle(session, session.Messages[0], maxPinned: 2);
        var second = PinPolicy.Toggle(session, session.Messages[1], maxPinned: 2);
        var cancel = PinPolicy.Toggle(session, session.Messages[0], maxPinned: 2);

        Assert.True(first.Changed);
        Assert.Equal(1, first.PinnedCount);
        Assert.Equal(2, second.PinnedCount);
        Assert.True(cancel.Changed);
        Assert.False(cancel.Pinned);
        Assert.Equal(1, cancel.PinnedCount);
        Assert.Equal(1, PinPolicy.Count(session));
    }

    [Fact]
    public void 达到上限时拒绝新的pin并给出提示()
    {
        var session = SessionWith(3);
        session.Messages[0].IsPinned = true;
        session.Messages[1].IsPinned = true;

        var result = PinPolicy.Toggle(session, session.Messages[2], maxPinned: 2);

        Assert.False(result.Changed);
        Assert.False(session.Messages[2].IsPinned);
        Assert.Equal(2, result.PinnedCount);
        Assert.Contains("最多 pin 2 条", result.Message);
    }

    [Fact]
    public void 上限为零时任何pin都被拒绝()
    {
        var session = SessionWith(1);

        var result = PinPolicy.Toggle(session, session.Messages[0], maxPinned: 0);

        Assert.False(result.Changed);
        Assert.Contains("最多 pin 0 条", result.Message);
    }

    [Fact]
    public void 取消pin不受上限影响()
    {
        var session = SessionWith(2);
        session.Messages[0].IsPinned = true;
        session.Messages[1].IsPinned = true;

        var result = PinPolicy.Toggle(session, session.Messages[1], maxPinned: 2);

        Assert.True(result.Changed);
        Assert.Equal(1, result.PinnedCount);
    }
}
