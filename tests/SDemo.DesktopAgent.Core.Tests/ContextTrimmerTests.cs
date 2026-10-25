using System.IO;
using System.Text;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>L1 确定性裁剪（§3.5）：只搬动请求里的消息，不改领域对象。</summary>
public sealed class ContextTrimmerTests : IDisposable
{
    private readonly string _root;
    private readonly HeuristicTokenCounter _counter = new();
    private readonly ContextTrimmer _trimmer = new();

    public ContextTrimmerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-trimmer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private TrimOptions Options(
        int messageBudget,
        int keepRecentTurns = 6,
        int toolTraceBudget = 300,
        int attachmentChars = 8192,
        IReadOnlySet<string>? pinned = null,
        int maxPinned = 20,
        int pinnedTruncate = 500) => new(
        MessageBudget: messageBudget,
        TotalBudget: messageBudget,
        KeepRecentTurns: keepRecentTurns,
        Counter: _counter,
        CalibrationFactor: 1.0,
        PinnedMessageIds: pinned,
        MaxPinnedMessages: maxPinned,
        PinnedShareLimit: 0.3,
        PinnedTruncateChars: pinnedTruncate,
        ToolTraceTokenBudget: toolTraceBudget,
        AttachmentInlineChars: attachmentChars);

    private static ChatMessage User(string text) => new(ChatMessageRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatMessageRole.Assistant, text);

    [Fact]
    public void 保留最近轮次并从最旧整轮淘汰()
    {
        var messages = new List<ChatMessage>();
        for (var index = 0; index < 6; index++)
        {
            messages.Add(User($"问题 {index} " + new string('问', 80)));
            messages.Add(Assistant($"回答 {index} " + new string('答', 80)));
        }

        var result = _trimmer.Trim(messages, Options(messageBudget: 200, keepRecentTurns: 1), AgentMessageMapper.Render);

        // 最近 1 轮（user + assistant）保留，其余 10 条省略。
        Assert.Equal(1, result.Stats.KeptTurns);
        Assert.Equal(6, result.Stats.TotalTurns);
        Assert.Equal(10, result.Stats.OmittedMessages);
        Assert.Equal(messages[^2].Content, result.Messages[0].Source.Content);
        Assert.Equal(messages[^1].Content, result.Messages[^1].Source.Content);
        Assert.NotNull(result.Messages[0].OmissionNote);
        Assert.Contains("省略", result.Messages[0].OmissionNote);
    }

    [Fact]
    public void 当前输入永远保留_即使单条就超预算()
    {
        var messages = new List<ChatMessage>
        {
            User("旧问题"),
            Assistant("旧回答"),
            User(new string('大', 4000))
        };

        var result = _trimmer.Trim(messages, Options(messageBudget: 60, keepRecentTurns: 1), AgentMessageMapper.Render);

        Assert.Single(result.Messages);
        Assert.Equal(messages[^1].Content, result.Messages[0].Source.Content);
        Assert.True(result.OverBudget);
    }

    [Fact]
    public void 历史附件被改成桩_内容仍可再读()
    {
        var attachment = new ChatAttachment
        {
            FileName = "big.txt",
            FilePath = Path.Combine(_root, "big.txt"),
            Size = 4096
        };

        File.WriteAllText(attachment.FilePath, new string('x', 4096));

        var old = new ChatMessage(ChatMessageRole.User, "第一轮带附件")
        {
            Attachments = [attachment]
        };

        var messages = new List<ChatMessage>
        {
            old,
            Assistant("收到"),
            User("第二轮继续")
        };

        var result = _trimmer.Trim(
            messages,
            Options(messageBudget: 120, keepRecentTurns: 1, attachmentChars: 512),
            AgentMessageMapper.Render);

        Assert.True(result.Stats.AttachmentsStubbed);
        var kept = result.Messages.First(message => message.Source == old);
        Assert.Contains("[附件已省略", kept.Rendered.Text);
        Assert.Contains(attachment.FilePath, kept.Rendered.Text);
    }

    [Fact]
    public void 工具轨迹按token预算回放_放不下的省略()
    {
        var reply = new ChatMessage(ChatMessageRole.Assistant, "已执行");
        for (var index = 0; index < 12; index++)
        {
            reply.ToolTrace.Add(new ToolTraceEntry(
                "powershell",
                ToolTraceEntry.StatusOk,
                ExitCode: 0,
                ArgsDigest: "scripts/run.ps1",
                Summary: $"第 {index} 次执行成功，输出了一小段日志",
                OutputChars: 200,
                At: DateTime.Now));
        }

        var messages = new List<ChatMessage>
        {
            User("执行脚本"),
            reply,
            User("结果呢")
        };

        var result = _trimmer.Trim(
            messages,
            Options(messageBudget: 400, keepRecentTurns: 2, toolTraceBudget: 60),
            AgentMessageMapper.Render);

        var kept = result.Messages.First(message => message.Source == reply);
        Assert.NotNull(kept.Rendered.TraceText);
        Assert.Contains("[工具轨迹]", kept.Rendered.Text);
        Assert.True(result.Stats.TraceTokens > 0);
    }

    [Fact]
    public void pin消息不会被淘汰_超量时截断()
    {
        var pinned = User("必须记住的约束：" + new string('重', 800));
        pinned.IsPinned = true;

        var messages = new List<ChatMessage> { pinned };
        for (var index = 0; index < 4; index++)
        {
            messages.Add(Assistant($"中间回答 {index} " + new string('答', 100)));
            messages.Add(User($"后续问题 {index} " + new string('问', 100)));
        }

        var result = _trimmer.Trim(
            messages,
            Options(messageBudget: 120, keepRecentTurns: 1, pinned: new HashSet<string> { pinned.Id }, pinnedTruncate: 40),
            AgentMessageMapper.Render);

        var kept = Assert.Single(result.Messages, message => message.Source == pinned);
        Assert.True(kept.IsPinned);
        Assert.True(result.Stats.PinnedCount >= 1);
        Assert.True(result.Stats.PinnedTruncated);
        Assert.Contains("截断", kept.Rendered.Text);
    }

    // ---------- P2b「先修剪，后摘要」 ----------

    [Fact]
    public void 先修剪工具轨迹再淘汰整轮历史()
    {
        var messages = new List<ChatMessage>();

        for (var turn = 0; turn < 4; turn++)
        {
            messages.Add(User($"第 {turn} 轮：" + new string('问', 40)));
            var reply = Assistant($"第 {turn} 轮回答：" + new string('答', 40));

            for (var index = 0; index < 12; index++)
            {
                reply.ToolTrace.Add(new ToolTraceEntry(
                    "powershell",
                    ToolTraceEntry.StatusOk,
                    ExitCode: 0,
                    ArgsDigest: $"scripts/run-{turn}-{index}.ps1",
                    Summary: "执行成功，输出了一小段日志",
                    OutputChars: 200,
                    At: DateTime.Now));
            }

            messages.Add(reply);
        }

        // 预算刚好够「正文」不够「正文 + 轨迹」：旧实现会先淘汰整轮，新实现先把轨迹修到放得下。
        var contentOnly = messages.Sum(message => _counter.CountMessage(
            message.Role == ChatMessageRole.User ? "user" : "assistant",
            message.Content));

        var result = _trimmer.Trim(
            messages,
            Options(messageBudget: contentOnly + 40 + 8, keepRecentTurns: 10, toolTraceBudget: 300),
            AgentMessageMapper.Render);

        Assert.Equal(0, result.Stats.OmittedMessages);
        Assert.Equal(result.Stats.TotalTurns, result.Stats.KeptTurns);
        Assert.True(result.Stats.PrunedTraceTokens > 0, "应当靠修剪轨迹进预算");
        Assert.True(result.Stats.Pruned);
    }

    [Fact]
    public void 修剪放不下时才淘汰整轮()
    {
        var messages = new List<ChatMessage>();

        for (var turn = 0; turn < 4; turn++)
        {
            messages.Add(User($"第 {turn} 轮：" + new string('问', 400)));
            messages.Add(Assistant($"第 {turn} 轮回答：" + new string('答', 400)));
        }

        // 正文本身就远超预算：轨迹一刀也修不出空间，只能整轮淘汰（旧的兜底路径仍然有效）。
        var result = _trimmer.Trim(
            messages,
            Options(messageBudget: 400, keepRecentTurns: 1),
            AgentMessageMapper.Render);

        Assert.True(result.Stats.OmittedMessages > 0);
        Assert.Equal(1, result.Stats.KeptTurns);
        Assert.Equal(0, result.Stats.PrunedTraceTokens);
    }

    [Fact]
    public void 宿主上下文快照永不被淘汰()
    {
        var messages = new List<ChatMessage>();

        for (var turn = 0; turn < 6; turn++)
        {
            messages.Add(User($"第 {turn} 轮：" + new string('问', 200)));
            messages.Add(Assistant($"第 {turn} 轮回答：" + new string('答', 200)));
        }

        messages.Add(User(HostSnapshot.Compose("### 时间\n2026-10-25T12:00:00+08:00")));

        var result = _trimmer.Trim(
            messages,
            Options(messageBudget: 120, keepRecentTurns: 1),
            AgentMessageMapper.Render);

        var snapshot = Assert.Single(
            result.Messages,
            message => HostSnapshot.IsSnapshot(message.Source.Content));
        Assert.Contains("宿主上下文快照", snapshot.Rendered.Text);
    }

    public void Dispose()
    {
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
