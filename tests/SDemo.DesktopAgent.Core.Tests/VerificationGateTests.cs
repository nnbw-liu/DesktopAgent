using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>验证门：无编辑不标、编辑后未验证标、成功清除、失败保留。</summary>
public sealed class VerificationGateTests
{
    [Fact]
    public void 没有编辑轨迹不标()
    {
        var session = ChatSession.CreateEmpty();

        Assert.False(VerificationGate.Evaluate(session).Dirty);
    }

    [Fact]
    public void 编辑后没有验证为dirty()
    {
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(Message(new DateTime(2026, 10, 22, 10, 0, 0), "a.cs"));

        var state = VerificationGate.Evaluate(session);

        Assert.True(state.Dirty);
        Assert.Equal(1, state.ChangedFiles);
    }

    [Fact]
    public void 成功验证在编辑后清除()
    {
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(Message(new DateTime(2026, 10, 22, 10, 0, 0), "a.cs"));
        session.LastVerification = Record(success: true, new DateTime(2026, 10, 22, 10, 1, 0));

        Assert.False(VerificationGate.Evaluate(session).Dirty);
    }

    [Fact]
    public void 验证失败仍为dirty()
    {
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(Message(new DateTime(2026, 10, 22, 10, 0, 0), "a.cs"));
        session.LastVerification = Record(success: false, new DateTime(2026, 10, 22, 10, 1, 0));

        var state = VerificationGate.Evaluate(session);

        Assert.True(state.Dirty);
        Assert.True(state.LastVerificationFailed);
    }

    [Fact]
    public void 同一时刻边界不算dirty()
    {
        var at = new DateTime(2026, 10, 22, 10, 0, 0);
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(Message(at, "a.cs"));
        session.LastVerification = Record(success: true, at);

        Assert.False(VerificationGate.Evaluate(session).Dirty);
    }

    private static ChatMessage Message(DateTime at, string path)
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, "done");
        message.ToolTrace.Add(new ToolTraceEntry(
            "apply_patch",
            ToolTraceEntry.StatusOk,
            null,
            path,
            "ok",
            10,
            at,
            [new EditedFile(path, 1, 1)]));
        return message;
    }

    private static VerificationRecord Record(bool success, DateTime at) =>
        new(
            VerificationRecord.BuildKind,
            "dotnet build",
            success,
            success ? 0 : 1,
            success ? 0 : 1,
            0,
            0,
            0,
            [],
            at,
            100);
}
