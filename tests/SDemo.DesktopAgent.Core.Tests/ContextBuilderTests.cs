using System.IO;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>单一上下文入口（§6.1）：预算、固定块、分段报告与超限信号。</summary>
public sealed class ContextBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly HeuristicTokenCounter _counter = new();
    private readonly StubSettingsService _settings = new();

    public ContextBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-builder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private ContextBuilder CreateBuilder(TokenCalibrationStore? calibration = null) =>
        new(_counter, _settings, calibration);

    private static ModelProvider Provider(string modelId = "m", string? preset = "8K") => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models =
        [
            new ModelEntry
            {
                Id = modelId,
                Profile = preset is null ? null : ModelProfilePresets.FromPreset(preset)
            }
        ]
    };

    private ContextBuildRequest Request(
        IReadOnlyList<ChatMessage> messages,
        string? skillText = null,
        bool passthrough = false,
        IReadOnlySet<string>? pinned = null,
        double scale = 1.0) => new(
        Provider(),
        "m",
        messages,
        SkillName: skillText is null ? null : "演示",
        SkillText: skillText,
        PinnedMessageIds: pinned,
        Passthrough: passthrough,
        BudgetScale: scale);

    [Fact]
    public void 报告分段之和等于估算总量且不超过预算()
    {
        var messages = new List<ChatMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(new ChatMessage(ChatMessageRole.User, $"问题 {index}"));
            messages.Add(new ChatMessage(ChatMessageRole.Assistant, $"回答 {index}"));
        }

        var plan = CreateBuilder().Build(Request(messages));

        Assert.Equal(plan.Report.Estimated, plan.Report.Segments.Total);
        Assert.True(plan.Report.Estimated <= plan.Report.Total);
        Assert.True(plan.Report.Segments.System > 0);
        Assert.True(plan.Report.Segments.Turns > 0);
        Assert.Equal(plan.Budget.Total, plan.Report.Total);
    }

    [Fact]
    public void 超出预算且无法再裁时抛需要压缩()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRole.User, new string('长', 40_000))
        };

        var error = Assert.Throws<ContextBudgetException>(() => CreateBuilder().Build(Request(messages)));

        Assert.Equal(AgentErrorKind.NeedsCompaction, error.Kind);
        Assert.NotNull(error.Report);
        Assert.True(error.Report!.Estimated > error.Report.Total);
        Assert.Contains("预算", error.Message);
    }

    [Fact]
    public void 技能超长时截断并标记()
    {
        var messages = new List<ChatMessage> { new(ChatMessageRole.User, "用技能处理") };
        var skill = new string('技', 20_000);

        var plan = CreateBuilder().Build(Request(messages, skill));

        Assert.Contains("本轮使用的技能", plan.Instructions);
        Assert.Contains("已截断", plan.Instructions);
        Assert.True(plan.Instructions.Length < skill.Length);
        Assert.True(plan.Report.Trimmed);
        Assert.True(plan.Report.Segments.Skills >= 0);
    }

    [Fact]
    public void 直通模式保留全部消息且不裁剪()
    {
        var messages = new List<ChatMessage>();
        for (var index = 0; index < 30; index++)
        {
            messages.Add(new ChatMessage(ChatMessageRole.User, new string('长', 500)));
        }

        var plan = CreateBuilder().Build(Request(messages, passthrough: true));

        Assert.Equal(30, plan.Messages.Count);
        Assert.True(plan.Report.Passthrough);
        Assert.False(plan.Report.Trimmed);
    }

    [Fact]
    public void 窗口为8K且附件50KB时仍能发出()
    {
        var attachment = new ChatAttachment
        {
            FileName = "manual.txt",
            FilePath = Path.Combine(_root, "manual.txt"),
            Size = 50 * 1024
        };

        File.WriteAllText(attachment.FilePath, new string('a', 50 * 1024));

        var messages = new List<ChatMessage>
        {
            new(ChatMessageRole.User, "总结这个文件")
            {
                Attachments = [attachment]
            }
        };

        var plan = CreateBuilder().Build(Request(messages));

        // 1 条用户消息 + 1 条宿主上下文快照（P2a：快照永远是最后一条）。
        Assert.Equal(2, plan.Messages.Count);
        Assert.True(plan.Report.Estimated <= plan.Report.Total);
        Assert.Contains("【附件：", plan.Messages[0].Text);
        // 内联被 48K 总量和窗口预算同时限制，不可能把 50KB 全塞进去。
        Assert.True(plan.Messages[0].Text.Length < 50 * 1024);
    }

    [Fact]
    public void 历史附件超预算时只留桩()
    {
        var attachment = new ChatAttachment
        {
            FileName = "old.txt",
            FilePath = Path.Combine(_root, "old.txt"),
            Size = 20 * 1024
        };

        File.WriteAllText(attachment.FilePath, new string('b', 20 * 1024));

        var messages = new List<ChatMessage>
        {
            new(ChatMessageRole.User, "第一轮带附件")
            {
                Attachments = [attachment]
            },
            new(ChatMessageRole.Assistant, "收到"),
            new(ChatMessageRole.User, "第二轮继续")
        };

        var plan = CreateBuilder().Build(Request(messages));

        Assert.True(plan.Report.AttachmentsStubbed);
        Assert.Contains("[附件已省略", plan.Messages[0].Text);
    }

    [Fact]
    public void 校准因子参与预算与分段()
    {
        var calibration = new TokenCalibrationStore(Path.Combine(_root, "calibration.json"));
        calibration.RecordUsage("p", "m", 1000, 1300);

        var messages = new List<ChatMessage> { new(ChatMessageRole.User, "你好") };
        var plan = CreateBuilder(calibration).Build(Request(messages));

        Assert.True(plan.Report.CalibrationFactor > 1.0);
        Assert.Equal(plan.Report.Estimated, plan.Report.Segments.Total);
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
