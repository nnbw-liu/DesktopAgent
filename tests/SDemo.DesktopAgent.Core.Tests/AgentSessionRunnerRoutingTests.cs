using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// M4：Runner 只在规则不确定时问小模型；失败 / 超时 / 关闭设置都回落规则，
/// 并且预览与发送复用同一份路由缓存。
/// </summary>
public sealed class AgentSessionRunnerRoutingTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    [Fact]
    public async Task 规则不确定时采用小模型建议()
    {
        var advisor = new FakeAdvisor(new RoutingAdvice(["pdf_read", "read_file"], "pdf", "需要读 PDF", 0.9));
        var chat = new RecordingChat();
        var runner = CreateRunner(chat, advisor);

        await foreach (var _ in runner.StreamAsync(Request("帮我处理一下那个东西"), new ContextRunTelemetry()))
        {
        }

        var routing = chat.Plans[0].Context.Report.Routing;
        Assert.NotNull(routing);
        Assert.Equal(RoutingDecision.SmallModelSource, routing!.Source);
        Assert.Contains("pdf_read", routing.ExposedTools);
        Assert.Equal(1, advisor.Calls);
    }

    [Fact]
    public async Task 小模型失败时回落规则并记录原因()
    {
        var advisor = new FakeAdvisor(advice: null);
        var chat = new RecordingChat();
        var runner = CreateRunner(chat, advisor);

        await foreach (var _ in runner.StreamAsync(Request("帮我处理一下那个东西"), new ContextRunTelemetry()))
        {
        }

        var routing = chat.Plans[0].Context.Report.Routing;
        Assert.NotNull(routing);
        Assert.Equal(RoutingDecision.RulesSource, routing!.Source);
        Assert.Contains("回落规则", routing.Reason);
        Assert.Contains("read_file", routing.ExposedTools);
    }

    [Fact]
    public async Task 关闭设置或Compact模式不咨询小模型()
    {
        var advisor = new FakeAdvisor(new RoutingAdvice(["pdf_read"]));
        var chat = new RecordingChat();
        var runner = CreateRunner(chat, advisor);

        _settings.Current.Agent.Mode = AgentModePreference.Compact;
        await foreach (var _ in runner.StreamAsync(Request("帮我处理一下那个东西"), new ContextRunTelemetry()))
        {
        }

        Assert.Equal(0, advisor.Calls);

        _settings.Current.Agent.Mode = AgentModePreference.Full;
        _settings.Current.Agent.SmallModelRoutingEnabled = false;
        await foreach (var _ in runner.StreamAsync(Request("帮我处理一下那个东西"), new ContextRunTelemetry()))
        {
        }

        Assert.Equal(0, advisor.Calls);
    }

    [Fact]
    public async Task 小模型超时回落规则并标注超时()
    {
        var advisor = new ThrowingAdvisor();
        var chat = new RecordingChat();
        var runner = CreateRunner(chat, advisor);

        await foreach (var _ in runner.StreamAsync(Request("帮我处理一下那个东西"), new ContextRunTelemetry()))
        {
        }

        var routing = chat.Plans[0].Context.Report.Routing;
        Assert.Equal(RoutingDecision.RulesSource, routing?.Source);
        Assert.Contains("超时", routing!.Reason);
    }

    [Fact]
    public async Task 预览与发送共享同一份路由缓存()
    {
        var transport = new FixedTransport("""{"tools":["pdf_read","read_file"],"intent":"pdf","confidence":0.9}""");
        var advisor = new SmallModelRoutingAdvisor(transport);
        var chat = new RecordingChat();
        var runner = CreateRunner(chat, advisor);

        var preview = await runner.PreviewAsync(Request("帮我处理一下那个东西"));
        await foreach (var _ in runner.StreamAsync(Request("帮我处理一下那个东西"), new ContextRunTelemetry()))
        {
        }

        Assert.Equal(RoutingDecision.SmallModelSource, preview.Report.Routing?.Source);
        Assert.Equal(RoutingDecision.SmallModelSource, chat.Plans[0].Context.Report.Routing?.Source);
        Assert.Equal(1, transport.Calls);
    }

    private AgentSessionRunner CreateRunner(IAgentChatService chat, IRoutingAdvisor advisor)
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        var builder = new ContextBuilder(_counter, _settings);
        return new AgentSessionRunner(
            chat,
            builder,
            _settings,
            tools: new AgentToolRegistry([new WidePack()]),
            routingAdvisor: advisor);
    }

    private AgentRunRequest Request(string text) => new(
        [new ChatMessage(ChatMessageRole.User, text)],
        Provider(),
        "m",
        new HashSet<string>(StringComparer.Ordinal) { "wide" });

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
    };

    private sealed class FakeAdvisor(RoutingAdvice? advice) : IRoutingAdvisor
    {
        public int Calls { get; private set; }

        public Task<RoutingAdvice?> AdviseAsync(
            RoutingAdviceRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(advice);
        }
    }

    private sealed class ThrowingAdvisor : IRoutingAdvisor
    {
        public Task<RoutingAdvice?> AdviseAsync(
            RoutingAdviceRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<RoutingAdvice?>(new OperationCanceledException("timeout"));
    }

    private sealed class FixedTransport(string reply) : IRoutingChatTransport
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            ModelProvider provider,
            string modelId,
            string systemPrompt,
            string userPrompt,
            string jsonSchema,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reply);
        }
    }

    private sealed class RecordingChat : IAgentChatService
    {
        public List<AgentRunPlan> Plans { get; } = [];

        public async IAsyncEnumerable<AgentDelta> RunAsync(
            AgentRunPlan plan,
            ContextRunTelemetry? telemetry = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Plans.Add(plan);
            await Task.Yield();
            yield return new AgentDelta("ok", string.Empty);
        }
    }

    /// <summary>足够多工具，保证 8K Full 下规则会截断、进入「不确定」分支。</summary>
    private sealed class WidePack : IAgentToolPack
    {
        private static readonly string[] Names =
        [
            "read_file", "search_files", "apply_patch", "run_command", "todo_write", "write_file",
            "pdf_read", "pdf_info", "run_powershell", "web_search", "search_workspace_memory",
            "read_archived_session"
        ];

        public AgentToolPackDescriptor Descriptor { get; } = new(
            "wide",
            "宽集合",
            "测试用工具集合。",
            "■",
            AgentToolRisk.Low,
            Order: 1,
            DefaultEnabled: true,
            WindowsOnly: false,
            Names.Select(name => new AgentToolFunctionDescriptor(name, name)).ToArray(),
            BuiltIn: true);

        public IReadOnlyList<AITool> Create(ToolContext context) =>
            Names.Select(name => (AITool)AIFunctionFactory.Create(() => "ok", name: name)).ToArray();
    }
}
