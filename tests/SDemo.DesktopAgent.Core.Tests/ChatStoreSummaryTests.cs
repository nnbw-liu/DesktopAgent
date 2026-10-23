using System.IO;
using Microsoft.Data.Sqlite;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 摘要持久化：写入 / 读回 / 覆盖范围失真时归零。</summary>
public sealed class ChatStoreSummaryTests : IDisposable
{
    private readonly string _root;

    public ChatStoreSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-chatstore-summary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task 摘要与覆盖范围落库后能读回()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession { Id = "s1", Title = "摘要会话" };

        for (var index = 0; index < 6; index++)
        {
            session.Messages.Add(new ChatMessage(
                index % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                $"消息 {index}"));
        }

        session.Summary = "## 目标与约束\n- 不要动数据库 [m0]\n## 已确认决策\n- 无\n## 关键事实\n- 无\n## 已改动\n- 无\n## 未完成\n- 无\n## 用户偏好\n- 无";
        session.SummaryThroughOrder = 4;
        session.SummaryTokens = 42;
        session.SummaryUpdatedAt = new DateTime(2026, 10, 22, 19, 0, 0);

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));

        Assert.Equal(4, loaded.SummaryThroughOrder);
        Assert.Equal(42, loaded.SummaryTokens);
        Assert.Equal(session.Summary, loaded.Summary);
        Assert.Equal(session.SummaryUpdatedAt, loaded.SummaryUpdatedAt);
        Assert.True(loaded.HasSummary);
        Assert.False(loaded.SummaryIsFallback);
    }

    [Fact]
    public async Task 兜底摘要在重启后仍能识别()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession { Id = "s2", Title = "兜底会话" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "一"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "二"));
        session.Summary = SummaryFormat.FallbackMarker + "\n## 目标与约束\n- 无";
        session.SummaryThroughOrder = 1;

        await store.SaveSessionAsync(session);

        var loaded = Assert.Single(await store.LoadAllAsync([]));

        Assert.True(loaded.SummaryIsFallback);
    }

    [Fact]
    public async Task 覆盖范围大于消息数时摘要归零()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession { Id = "s3", Title = "越界会话" };
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "只有一条"));
        session.Summary = "## 目标与约束\n- 无";
        session.SummaryThroughOrder = 99;

        await store.SaveSessionAsync(session);

        Assert.Equal(0, session.SummaryThroughOrder);
        Assert.Null(session.Summary);

        var loaded = Assert.Single(await store.LoadAllAsync([]));
        Assert.False(loaded.HasSummary);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
