using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>应用层入口（§3.1）：预览、校准、溢出重试与「出字后不重试」。</summary>
public sealed class AgentSessionRunnerTests
{
    private readonly StubSettingsService _settings = new();
    private readonly HeuristicTokenCounter _counter = new();

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models = [new ModelEntry { Id = "m", Profile = ModelProfilePresets.FromPreset("8K") }]
    };

    private AgentRunRequest Request(
        IReadOnlyList<ChatMessage>? messages = null,
        AgentRunMetrics? metrics = null) => new(
        messages ?? [new ChatMessage(ChatMessageRole.User, "你好")],
        Provider(),
        "m",
        new HashSet<string>(StringComparer.Ordinal),
        SessionId: "s1",
        Metrics: metrics);

    private (AgentSessionRunner Runner, TokenCalibrationStore Calibration) CreateRunner(
        IAgentChatService chat,
        string root)
    {
        var calibration = new TokenCalibrationStore(Path.Combine(root, "calibration.json"));
        var builder = new ContextBuilder(_counter, _settings, calibration);
        return (new AgentSessionRunner(chat, builder, _settings, calibration: calibration), calibration);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdemo-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void 预览只组装上下文不发请求()
    {
        var chat = new ScriptedChat([Script.Text("ok")]);
        var (runner, _) = CreateRunner(chat, TempRoot());

        var plan = runner.Preview(Request());

        Assert.Equal(0, chat.Calls);
        Assert.True(plan.Report.Total > 0);
        Assert.Equal("m", plan.ModelId);
    }

    [Fact]
    public async Task 溢出时按半预算重试一次并记下窗口()
    {
        var chat = new ScriptedChat([Script.Overflow(4096), Script.Text("重试成功")]);
        var (runner, calibration) = CreateRunner(chat, TempRoot());
        var telemetry = new ContextRunTelemetry();

        var deltas = new List<AgentDelta>();
        await foreach (var delta in runner.StreamAsync(Request(), telemetry))
        {
            deltas.Add(delta);
        }

        Assert.Equal(2, chat.Calls);
        Assert.True(telemetry.OverflowRetried);
        Assert.Contains("4.0K", telemetry.ContextNotice);
        Assert.Equal(4096, telemetry.Report!.LearnedWindow);
        Assert.Equal(4096, calibration.GetWindowOverride("p", "m"));
        Assert.Equal("重试成功", string.Concat(deltas.Select(delta => delta.Text)));
    }

    [Fact]
    public async Task 重试用更小的预算()
    {
        var chat = new ScriptedChat([Script.Overflow(null), Script.Text("ok")]);
        var (runner, _) = CreateRunner(chat, TempRoot());
        var telemetry = new ContextRunTelemetry();

        await foreach (var _ in runner.StreamAsync(Request(), telemetry))
        {
        }

        Assert.Equal(2, chat.Plans.Count);
        Assert.True(chat.Plans[1].Context.Report.Total < chat.Plans[0].Context.Report.Total);
    }

    [Fact]
    public async Task 已经输出正文后不再重试()
    {
        var chat = new ScriptedChat([Script.TextThenOverflow()]);
        var (runner, _) = CreateRunner(chat, TempRoot());
        var telemetry = new ContextRunTelemetry();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runner.StreamAsync(Request(), telemetry))
            {
            }
        });

        Assert.Contains("maximum context length", error.Message);
        Assert.Equal(1, chat.Calls);
        Assert.False(telemetry.OverflowRetried);
    }

    [Fact]
    public async Task 超限时直接报错且不调用模型()
    {
        var chat = new ScriptedChat([]);
        var (runner, _) = CreateRunner(chat, TempRoot());
        var telemetry = new ContextRunTelemetry();
        var request = Request([new ChatMessage(ChatMessageRole.User, new string('长', 40_000))]);

        var error = await Assert.ThrowsAsync<ContextBudgetException>(async () =>
        {
            await foreach (var _ in runner.StreamAsync(request, telemetry))
            {
            }
        });

        Assert.Equal(AgentErrorKind.NeedsCompaction, error.Kind);
        Assert.Equal(0, chat.Calls);
        Assert.NotNull(telemetry.ContextNotice);
    }

    [Fact]
    public async Task 成功请求后写入实际usage并校准()
    {
        var chat = new ScriptedChat([Script.Text("ok")]);
        var (runner, calibration) = CreateRunner(chat, TempRoot());
        var metrics = new AgentRunMetrics();
        var telemetry = new ContextRunTelemetry();

        await foreach (var delta in runner.StreamAsync(Request(metrics: metrics), telemetry))
        {
            // 假模型在吐字前写入 usage，模拟服务端返回。
            metrics.AddUsage(new UsageDetails { InputTokenCount = (int)(telemetry.Report!.Estimated * 1.5) });
        }

        Assert.True(telemetry.Report!.ActualInputTokens > 0);
        Assert.True(calibration.GetFactor("p", "m") > 1.0);
    }

    /// <summary>脚本化的假 chat：按顺序执行，记录收到的 plan。</summary>
    private sealed class ScriptedChat : IAgentChatService
    {
        private readonly Queue<Func<AgentRunPlan, ContextRunTelemetry, IEnumerable<AgentDelta>>> _steps;

        public ScriptedChat(IEnumerable<Func<AgentRunPlan, ContextRunTelemetry, IEnumerable<AgentDelta>>> steps) =>
            _steps = new Queue<Func<AgentRunPlan, ContextRunTelemetry, IEnumerable<AgentDelta>>>(steps);

        public int Calls { get; private set; }

        public List<AgentRunPlan> Plans { get; } = [];

        public async IAsyncEnumerable<AgentDelta> RunAsync(
            AgentRunPlan plan,
            ContextRunTelemetry? telemetry = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Plans.Add(plan);
            telemetry ??= new ContextRunTelemetry();
            telemetry.EstimatedInputTokens += plan.Context.Report.Estimated;

            var step = _steps.Count > 0 ? _steps.Dequeue() : null;
            if (step is null)
            {
                throw new InvalidOperationException("脚本已用尽");
            }

            foreach (var delta in step(plan, telemetry))
            {
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private static class Script
    {
        public static Func<AgentRunPlan, ContextRunTelemetry, IEnumerable<AgentDelta>> Text(string text) =>
            (_, _) => Yield(text);

        public static Func<AgentRunPlan, ContextRunTelemetry, IEnumerable<AgentDelta>> Overflow(int? window) =>
            (_, _) => ThrowOverflow(window);

        public static Func<AgentRunPlan, ContextRunTelemetry, IEnumerable<AgentDelta>> TextThenOverflow() =>
            (_, _) => YieldThenThrow();

        private static IEnumerable<AgentDelta> Yield(string text)
        {
            yield return new AgentDelta(text, string.Empty);
        }

        private static IEnumerable<AgentDelta> ThrowOverflow(int? window)
        {
            throw new InvalidOperationException(window is null
                ? "prompt is too long"
                : $"maximum context length is {window} tokens");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static IEnumerable<AgentDelta> YieldThenThrow()
        {
            yield return new AgentDelta("部分内容", string.Empty);
            throw new InvalidOperationException("maximum context length is 4096 tokens");
        }
    }
}
