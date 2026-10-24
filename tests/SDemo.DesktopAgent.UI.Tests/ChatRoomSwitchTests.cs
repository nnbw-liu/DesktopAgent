using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// 切换聊天室时的生成状态：
/// 1) 默认切走即中断，并把旧会话的「推理中」状态收回；
/// 2) 打开「继续生成」后切走不中断，回来后能接着看；
/// 3) 首个增量到达前，AI 回复里也显示思考状态。
/// </summary>
public sealed class ChatRoomSwitchTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();
    private readonly BlockingRunner _runner = new();

    public ChatRoomSwitchTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        ChatRoomFixture.Register(this, _settings, _runner, _temp.Root);
    }

    [Fact]
    public async Task 默认切走后中断生成并收回左侧推理状态()
    {
        var session = TaskSession();
        var generatingStates = new List<bool>();

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.OnGeneratingChanged,
                EventCallback.Factory.Create<(ChatSession Session, bool IsGenerating)>(
                    this, args =>
                    {
                        // 外层 AppShell 的职责：把状态写回会话，侧栏据此显示「推理中」。
                        args.Session.IsGenerating = args.IsGenerating;
                        generatingStates.Add(args.IsGenerating);
                    })));

        var run = cut.InvokeAsync(() =>
            cut.Instance.SubmitForTestAsync(new ComposerSubmission("帮我改一下 file.cs", [])));

        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(5));

        // 还没收到第一个增量：回复区域本身也要有「思考中」。
        cut.WaitForAssertion(() => Assert.Contains("思考中", cut.Find(".msg-thinking").TextContent));
        Assert.True(session.IsGenerating);

        // 切走：默认设置是中断。
        cut.SetParametersAndRender(parameters => parameters
            .Add(component => component.IsActive, false));

        cut.WaitForAssertion(() => Assert.False(session.IsGenerating));
        cut.WaitForAssertion(() => Assert.Contains(false, generatingStates));

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 打开继续生成后切走不中断且完成后恢复状态()
    {
        _settings.Current.ContinueGenerationOnChatSwitch = true;
        var session = TaskSession();
        var generatingStates = new List<bool>();

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.OnGeneratingChanged,
                EventCallback.Factory.Create<(ChatSession Session, bool IsGenerating)>(
                    this, args =>
                    {
                        args.Session.IsGenerating = args.IsGenerating;
                        generatingStates.Add(args.IsGenerating);
                    })));

        var run = cut.InvokeAsync(() =>
            cut.Instance.SubmitForTestAsync(new ComposerSubmission("帮我改一下 file.cs", [])));

        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _runner.ReleaseFirstDelta();

        // 等到思维链真正进入时间线，再模拟切走。
        cut.WaitForAssertion(() => Assert.Contains("思考中", cut.Find(".timeline-step--reasoning").TextContent));

        cut.SetParametersAndRender(parameters => parameters
            .Add(component => component.IsActive, false));

        // 后台继续：状态不能掉，回复也还没有结束。
        await Task.Delay(100);
        Assert.True(session.IsGenerating);
        Assert.False(cut.Instance.IsActive);

        _runner.ReleaseCompletion();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(() => Assert.False(session.IsGenerating));
        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);
        Assert.Contains("好了。", reply.Content, StringComparison.Ordinal);
        Assert.Contains(false, generatingStates);
    }

    private static ChatSession TaskSession()
    {
        var session = new ChatSession { Id = "switch", Title = "切换" };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "帮我改一下 file.cs"));
        return session;
    }

    /// <summary>
    /// 分两段放行的流：第一段前挂起（验证「思考中」），第二段前挂起（验证切走后还在后台跑）。
    /// 取消时会从挂起点抛出，和真实的流式取消一致。
    /// </summary>
    private sealed class BlockingRunner : IAgentSessionRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstDelta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void ReleaseFirstDelta() => _firstDelta.TrySetResult();

        public void ReleaseCompletion() => _completion.TrySetResult();

        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("预览不参与切换测试");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("预览不参与切换测试");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();

            await _firstDelta.Task.WaitAsync(cancellationToken);
            yield return new AgentDelta(string.Empty, "先分析一下调用链。");

            await _completion.Task.WaitAsync(cancellationToken);
            yield return new AgentDelta("好了。", string.Empty);
        }
    }
}
