using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Services;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;
using ChatMessageRole = SDemo.DesktopAgent.Models.ChatMessageRole;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>验证结果落库与状态包：重启后仍能读到最近一次。</summary>
public sealed class VerificationStoreTests
{
    [Fact]
    public async Task 验证结果保存并重载()
    {
        using var temp = new TempDirectory();
        var store = new ChatStore(Path.Combine(temp.Root, "chats"));
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "build"));
        session.LastVerification = Record();

        await store.SaveSessionAsync(session);
        var loaded = await store.LoadAllAsync([]);

        var restored = Assert.Single(loaded);
        Assert.NotNull(restored.LastVerification);
        Assert.Equal(1, restored.LastVerification!.Errors);
        Assert.Equal("dotnet build Demo.sln", restored.LastVerification.Command);
    }

    [Fact]
    public void 状态包显示最近验证()
    {
        using var temp = new TempDirectory();
        var session = ChatSession.CreateEmpty();
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "build"));
        session.LastVerification = Record();

        var state = new WorkspaceStateProvider().Build(temp.Root, session);

        Assert.NotNull(state.LastVerification);
        Assert.Contains("dotnet build Demo.sln", state.LastVerification);
        Assert.Contains("失败", state.LastVerification);
    }

    private static VerificationRecord Record() => new(
        VerificationRecord.BuildKind,
        "dotnet build Demo.sln",
        false,
        1,
        1,
        0,
        0,
        0,
        ["src/A.cs(1,1): error CS0001: bad"],
        DateTime.Parse("2026-10-22T10:45:00+08:00"),
        1200);
}
