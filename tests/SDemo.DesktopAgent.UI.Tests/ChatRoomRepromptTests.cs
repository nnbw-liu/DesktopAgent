using Bunit;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// R1.2：ChatRoom 的「无修改回灌」触发条件（用假 Runner）。
/// 假 Runner 只记录一次只读工具调用、不产生编辑，宿主应当追加提示并最多重试 3 次；
/// Compact 模式不打扰。
/// </summary>
public sealed class ChatRoomRepromptTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();
    private readonly FakeSessionRunner _runner = new();

    public ChatRoomRepromptTests()
    {
        _settings.Current.Providers.Add(new ModelProvider
        {
            Id = "test",
            DisplayName = "测试",
            BaseUrl = "https://example.com/v1",
            ApiKey = "k",
            Models = [new ModelEntry { Id = "m", Profile = new ModelProfile(32_768, 4096) }]
        });
        _settings.Current.LastProviderId = "test";
        _settings.Current.LastModelId = "m";

        ChatRoomFixture.Register(this, _settings, _runner, _temp.Root);
    }

    [Fact]
    public async Task 只读工具且没有编辑时追加宿主提示并重试()
    {
        var session = TaskSession();

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("帮我修改 file.cs 里的 bug", [])));

        // 初次 + 3 次回灌 = 4 次；第 4 次达到上限后不再追问。
        Assert.Equal(4, _runner.Calls);

        // 一次发送 = 一个回合：宿主追问不再新开回复，也不新增用户消息。
        Assert.Equal(2, session.Messages.Count);
        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);

        var prompts = reply.Steps
            .Where(step => step.Kind == ChatStepKind.HostPrompt)
            .ToList();

        Assert.Equal(3, prompts.Count);
        Assert.Contains("本轮没有检测到任何文件修改", prompts[0].Text);
        Assert.Contains("第 3/3 次提醒", prompts[2].Text);
        Assert.Contains("已完成。", reply.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 有成功编辑时不追加宿主提示()
    {
        _runner.SimulateSuccessfulEdit = true;
        var session = TaskSession();

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("帮我修改 file.cs 里的 bug", [])));

        Assert.Equal(1, _runner.Calls);

        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);
        Assert.DoesNotContain(reply.Steps, step => step.Kind == ChatStepKind.HostPrompt);
    }

    [Fact]
    public async Task Compact模式不触发回灌()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Compact;
        var session = TaskSession();

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("帮我修改 file.cs 里的 bug", [])));

        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task 纯问答不触发回灌()
    {
        var session = new ChatSession { Id = "qa", Title = "问答" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "解释一下这个文件做什么"));

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(new ComposerSubmission("解释一下这个文件做什么", [])));

        Assert.Equal(1, _runner.Calls);
    }

    private static ChatSession TaskSession()
    {
        var session = new ChatSession { Id = "task", Title = "任务" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "帮我修改 file.cs 里的 bug"));
        return session;
    }

    /// <summary>假 Runner：每轮记一次只读工具调用（没有 [EDIT]），其余什么都不做。</summary>
    private sealed class FakeSessionRunner : IAgentSessionRunner
    {
        public int Calls { get; private set; }

        public bool SimulateSuccessfulEdit { get; set; }

        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("预览不参与回灌测试");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("预览不参与回灌测试");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();

            request.Metrics?.RecordToolCall("read_file");
            request.Metrics?.RecordToolTrace(new ToolTraceEntry(
                "read_file",
                ToolTraceEntry.StatusOk,
                null,
                "file.cs",
                "读取 40 行",
                400,
                DateTime.Now));

            if (SimulateSuccessfulEdit)
            {
                // 编辑工具 + [EDIT] 行 = 「本轮确实改过文件」，回灌必须停止。
                request.Metrics?.RecordToolCall("apply_patch");
                request.Metrics?.RecordToolTrace(new ToolTraceEntry(
                    "apply_patch",
                    ToolTraceEntry.StatusOk,
                    null,
                    "file.cs",
                    "已写入 1 处",
                    120,
                    DateTime.Now,
                    [new EditedFile("file.cs", 1, 1)]));
            }

            telemetry.Report = null;
            yield return new AgentDelta("已完成。", string.Empty);
        }
    }

}
