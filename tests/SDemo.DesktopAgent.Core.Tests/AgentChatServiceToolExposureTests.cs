using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Agents;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// M1 端到端（无网络）：AgentChatService 必须按路由结果生成工具，
/// 再把实测 schema token 回填进报告，而不是沿用全量工具。
/// </summary>
public sealed class AgentChatServiceToolExposureTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    [Fact]
    public async Task 只把路由选中的工具交给模型并回填工具段()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry(
        [
            new TodoToolPack(),
            new FileToolPack(),
            new EditToolPack()
        ]);

        var runtime = new RecordingRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var telemetry = new ContextRunTelemetry();
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "todo", "files", "edits" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        await foreach (var _ in service.RunAsync(plan, telemetry))
        {
        }

        Assert.NotNull(runtime.Invocation);
        var names = runtime.Invocation!.Tools.Select(tool => tool.Name).ToArray();

        Assert.True(names.Length > 0);
        Assert.True(names.Length <= AgentSettings.SmallWindowMaxExposedTools);
        Assert.Contains("read_file", names);
        Assert.Contains(CapabilityRouter.EscapeHatchTool, names);
        Assert.True(telemetry.Report!.Segments.ToolSchema > 0);
        Assert.True(telemetry.Report.ToolSchemaTokens <= context.Budget.ToolSchemaLimit);
        Assert.Equal(telemetry.Report.Segments.Total, telemetry.Report.Estimated);
        Assert.NotNull(telemetry.Report.Routing);
    }

    [Fact]
    public async Task 全量暴露时不再附加兜底入口()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        _settings.Current.Agent.ExposeAllTools = true;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);

        var runtime = new RecordingRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var telemetry = new ContextRunTelemetry();
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        await foreach (var _ in service.RunAsync(plan, telemetry))
        {
        }

        var names = runtime.Invocation!.Tools.Select(tool => tool.Name).ToArray();
        Assert.Equal(6, names.Length);
        Assert.DoesNotContain(CapabilityRouter.EscapeHatchTool, names);
        Assert.True(telemetry.Report!.ToolSchemaTokens <= context.Budget.ToolSchemaLimit);
    }

    [Fact]
    public async Task 工具开始时先推送运行中状态再推送结果()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);
        var runtime = new ToolCallRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var metrics = new AgentRunMetrics();
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: metrics);

        var sawRunning = false;
        var deltaCount = 0;

        await foreach (var _ in service.RunAsync(plan))
        {
            deltaCount++;

            if (metrics.ToolTrace.Any(entry => entry.Status == ToolTraceEntry.StatusRunning))
            {
                sawRunning = true;
            }
        }

        Assert.True(sawRunning);
        Assert.True(deltaCount >= 2);
        var trace = Assert.Single(metrics.ToolTrace);
        Assert.Equal(ToolTraceEntry.StatusOk, trace.Status);
        Assert.Equal("read_file", trace.ToolId);
    }

    [Fact]
    public async Task 对话轮不生成工具并切换对话提示词()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);

        var runtime = new RecordingRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(
            Provider(),
            "m",
            [new ChatMessage(ChatMessageRole.User, "你好")]));
        var telemetry = new ContextRunTelemetry();
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        await foreach (var _ in service.RunAsync(plan, telemetry))
        {
        }

        Assert.NotNull(runtime.Invocation);
        Assert.Empty(runtime.Invocation!.Tools);
        Assert.Equal(SystemPrompts.Chat, runtime.Invocation.Instructions);
        Assert.False(telemetry.Report!.Routing!.NeedsTools);
        Assert.Equal(0, telemetry.Report.ToolSchemaTokens);
    }

    [Fact]
    public async Task 内置循环的_step_边界会跟着增量透传给界面()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);

        var runtime = new StepRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        var steps = new List<int>();

        await foreach (var delta in service.RunAsync(plan))
        {
            if (delta.Text.Length > 0 || delta.Reasoning.Length > 0)
            {
                steps.Add(delta.Step);
            }
        }

        // 两步的思考与正文各自归到自己的 step 上，而不是全算成「本轮」。
        Assert.Equal([0, 1], steps);
    }

    [Fact]
    public async Task 设置里打开内置循环时调用会带上_NativeLoop()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        _settings.Current.Agent.NativeLoop = true;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);

        var runtime = new RecordingRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        await foreach (var _ in service.RunAsync(plan))
        {
        }

        Assert.True(runtime.Invocation!.NativeLoop);
    }

    [Fact]
    public async Task 超长工具结果按设置落盘到工作区()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);

        var runtime = new RecordingRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var telemetry = new ContextRunTelemetry();
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        await foreach (var _ in service.RunAsync(plan, telemetry))
        {
        }

        // 直接跑一次真实的装饰器链：设置里的开关 → 落盘 seam → 工作区目录。
        Assert.NotNull(runtime.Invocation?.Decorate);
        var decorated = runtime.Invocation!.Decorate!(new NullChatClient());

        await decorated.GetResponseAsync(
        [
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, "系统提示"),
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "跑一下构建"),
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-9", "run_command", new Dictionary<string, object?>
                {
                    ["command"] = "dotnet"
                })
            ]),
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("call-9", new string('x', 200_000))])
        ]);

        var spill = Path.Combine(paths.DefaultWorkspaceRoot, ".agent", "spill");
        Assert.True(Directory.Exists(spill), "超长工具结果应当落到工作区 .agent/spill");
        Assert.Single(Directory.GetFiles(spill, "*.log"));
    }

    [Fact]
    public async Task 关掉落盘开关时超长工具结果只截断()
    {
        _settings.Current.Agent.Mode = AgentModePreference.Full;
        _settings.Current.Context.SpillLargeToolOutputs = false;
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var registry = new AgentToolRegistry([new FileToolPack()]);

        var runtime = new RecordingRuntime();
        var service = new AgentChatService(
            registry,
            new WorkspaceContext(paths),
            _settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(),
            runtime);

        var builder = new ContextBuilder(_counter, _settings);
        var context = builder.Build(new ContextBuildRequest(Provider(), "m", [Message()]));
        var plan = new AgentRunPlan(
            Provider(), "m", context, new HashSet<string> { "files" },
            SessionId: "s1", Metrics: new AgentRunMetrics());

        await foreach (var _ in service.RunAsync(plan))
        {
        }

        var decorated = runtime.Invocation!.Decorate!(new NullChatClient());
        await decorated.GetResponseAsync(
        [
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("call-9", new string('x', 200_000))])
        ]);

        Assert.False(Directory.Exists(Path.Combine(paths.DefaultWorkspaceRoot, ".agent", "spill")));
    }

    private static ChatMessage Message() => new(ChatMessageRole.User, "看一下这个文件");

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K")! }]
    };

    /// <summary>模拟一次工具调用：先发 FunctionCall，再发 FunctionResult，最后吐正文。</summary>
    private sealed class ToolCallRuntime : IAgentRuntime
    {
        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentInvocation invocation,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentStreamEvent.FunctionCall(
                "call-1",
                "read_file",
                new Dictionary<string, object?> { ["path"] = "README.md" });
            yield return new AgentStreamEvent.FunctionResult("call-1", "[TOOL_STATUS:ok]\n读取完成");
            yield return new AgentStreamEvent.Text("完成");
        }
    }

    /// <summary>模拟内置循环的两步：第一步思考，第二步才给正文。</summary>
    private sealed class StepRuntime : IAgentRuntime
    {
        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentInvocation invocation,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentStreamEvent.StepStarted(0, 3, DateTimeOffset.Now);
            yield return new AgentStreamEvent.Reasoning("先看文件。");
            yield return new AgentStreamEvent.StepCompleted(0, DateTimeOffset.Now);
            yield return new AgentStreamEvent.StepStarted(1, 3, DateTimeOffset.Now);
            yield return new AgentStreamEvent.Text("看完了。");
            yield return new AgentStreamEvent.StepCompleted(1, DateTimeOffset.Now);
        }
    }

    /// <summary>记录收到的调用（工具列表），然后直接吐一段正文。</summary>
    private sealed class RecordingRuntime : IAgentRuntime
    {
        public AgentInvocation? Invocation { get; private set; }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentInvocation invocation,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Invocation = invocation;
            await Task.Yield();
            yield return new AgentStreamEvent.Text("ok");
        }
    }

    /// <summary>只用来让装饰器链跑一次请求：不关心回应，请求体已经被装饰器处理过了。</summary>
    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
