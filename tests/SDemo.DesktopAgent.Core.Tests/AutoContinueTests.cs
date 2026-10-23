using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>自动续跑：dirty + 命令 + 开启时才提示，最多一次；关闭不续跑。</summary>
public sealed class AutoContinueTests
{
    [Fact]
    public void 有未验证改动且配置命令时续跑()
    {
        var session = DirtySession();

        var decision = AutoContinuePolicy.Decide(
            session,
            "dotnet build Demo.sln",
            null,
            new VerificationSettings { AutoVerifyAfterEdit = true, MaxAutoContinue = 1 },
            alreadyContinued: 0);

        Assert.True(decision.Continue);
        Assert.Contains("dotnet build Demo.sln", decision.Prompt);
        Assert.DoesNotContain("[TOOL_RESULT", decision.Prompt);
    }

    [Fact]
    public void 只续跑一次()
    {
        var session = DirtySession();

        var decision = AutoContinuePolicy.Decide(
            session,
            "dotnet build Demo.sln",
            null,
            new VerificationSettings { MaxAutoContinue = 1 },
            alreadyContinued: 1);

        Assert.False(decision.Continue);
    }

    [Fact]
    public void 关闭时不续跑()
    {
        var session = DirtySession();

        var decision = AutoContinuePolicy.Decide(
            session,
            "dotnet build Demo.sln",
            null,
            new VerificationSettings { AutoVerifyAfterEdit = false },
            alreadyContinued: 0);

        Assert.False(decision.Continue);
    }

    [Fact]
    public void 没有命令时不续跑()
    {
        var session = DirtySession();

        Assert.False(AutoContinuePolicy.Decide(
            session,
            null,
            null,
            new VerificationSettings(),
            alreadyContinued: 0).Continue);
    }

    private static ChatSession DirtySession()
    {
        var session = ChatSession.CreateEmpty();
        var message = new ChatMessage(ChatMessageRole.Assistant, "done");
        message.ToolTrace.Add(new ToolTraceEntry(
            "apply_patch",
            ToolTraceEntry.StatusOk,
            null,
            "a.cs",
            "ok",
            10,
            DateTime.Now,
            [new EditedFile("a.cs", 1, 1)]));
        session.Messages.Add(message);
        return session;
    }
}
