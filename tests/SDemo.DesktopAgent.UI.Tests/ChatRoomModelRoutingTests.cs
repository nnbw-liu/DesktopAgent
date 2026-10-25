using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>聊天室接上模型路由：一次发送用路由结果创建回复，并把目标模型传给 Runner。</summary>
public sealed class ChatRoomModelRoutingTests : TestContext
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySettings _settings = new();
    private readonly RecordingRunner _runner = new();
    private readonly FixedRouter _router = new();

    public ChatRoomModelRoutingTests()
    {
        _settings.Current.Providers.Add(new ModelProvider
        {
            Id = "test",
            DisplayName = "测试",
            BaseUrl = "https://example.com/v1",
            ApiKey = "k",
            Models =
            [
                new ModelEntry { Id = "m", Profile = new ModelProfile(32_768, 4096) },
                new ModelEntry { Id = "code-model", Profile = new ModelProfile(32_768, 4096) }
            ]
        });
        _settings.Current.LastProviderId = "test";
        _settings.Current.LastModelId = "m";
        _settings.Current.ModelRouting.Mode = ModelRoutingMode.Rules;

        ChatRoomFixture.Register(this, _settings, _runner, _temp.Root);
        // 覆盖 Fixture 里的真实路由器：组件测试不联网。
        Services.AddSingleton<IModelRouter>(_router);
    }

    [Fact]
    public async Task 自动模式下按路由结果创建回复并传给Runner()
    {
        var session = new ChatSession
        {
            Id = "routing",
            Title = "路由",
            ScopedSettings = new ScopedSettings { AutoModelRouting = true }
        };
        session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, "帮我修改 file.cs 里的 bug"));

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.InvokeAsync(() => cut.Instance.SubmitForTestAsync(
            new ComposerSubmission("帮我修改 file.cs 里的 bug", [])));

        Assert.True(_router.Calls >= 1);
        var reply = Assert.Single(session.Messages, message => message.Role == ChatMessageRole.Assistant);
        Assert.Equal("code-model", reply.ModelId);
        Assert.Equal("code-model", _runner.LastModelId);
    }

    private sealed class FixedRouter : IModelRouter
    {
        public int Calls { get; private set; }

        public Task<ModelRoutingDecision> RouteAsync(
            ModelRoutingRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var provider = request.Providers[0];
            return Task.FromResult(new ModelRoutingDecision(
                provider,
                "code-model",
                ModelTaskKind.Code,
                ModelRoutingSource.Rules,
                "测试路由",
                0.9,
                60));
        }

        public void ClearSession(string sessionId)
        {
        }
    }

    private sealed class RecordingRunner : IAgentSessionRunner
    {
        public string? LastModelId { get; private set; }

        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("测试不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("测试不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastModelId = request.ModelId;
            await Task.Yield();
            yield return new AgentDelta("完成：已修改。", string.Empty);
        }
    }
}
