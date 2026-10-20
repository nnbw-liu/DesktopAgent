using System.Runtime.CompilerServices;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>插件 Run 的请求组装：单轮、无工具、无历史（除非能力打开），用户输入单独成条。</summary>
public sealed class PluginRunnerTests
{
    [Fact]
    public async Task 组装单轮请求_skill进system_输入进user()
    {
        var chat = new StubChatService();
        var runner = new PluginRunner(chat, new PluginPromptComposer());
        var descriptor = BuildDescriptor();
        var mount = Assert.Single(descriptor.Mounts);
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" };
        var metrics = new AgentRunMetrics();

        var deltas = new List<AgentDelta>();
        await foreach (var delta in runner.RunComposerAsync(new PluginRunRequest(
                           descriptor,
                           mount,
                           values,
                           "hello world",
                           Provider(),
                           "test-model",
                           metrics)))
        {
            deltas.Add(delta);
        }

        Assert.Equal("译文", Assert.Single(deltas).Text);

        var plan = chat.Plan!;
        Assert.Equal("test-model", plan.ModelId);
        Assert.Contains("日本語（ja）", plan.Instructions);
        Assert.DoesNotContain("---", plan.Instructions);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(ChatMessageRole.User, message.Role);
        Assert.Contains(PluginPromptComposer.InputOpenTag, message.Content);
        Assert.Contains("hello world", message.Content);
        Assert.Contains(PluginPromptComposer.InputCloseTag, message.Content);

        Assert.Empty(plan.Tools);
        Assert.Same(metrics, plan.Metrics);
    }

    [Fact]
    public async Task 默认不带历史()
    {
        var chat = new StubChatService();
        var runner = new PluginRunner(chat, new PluginPromptComposer());
        var descriptor = BuildDescriptor();
        var history = new List<ChatMessage>
        {
            new(ChatMessageRole.User, "上一轮的问题"),
            new(ChatMessageRole.Assistant, "上一轮的回答")
        };

        await ConsumeAsync(runner, descriptor, history, allowHistory: false);

        Assert.Single(chat.Plan!.Messages);
    }

    [Fact]
    public async Task 能力打开历史时带上最近消息()
    {
        var chat = new StubChatService();
        var runner = new PluginRunner(chat, new PluginPromptComposer());
        var descriptor = BuildDescriptor(allowHistory: true);
        var history = new List<ChatMessage>
        {
            new(ChatMessageRole.User, "上一轮的问题"),
            new(ChatMessageRole.Assistant, "上一轮的回答")
        };

        await ConsumeAsync(runner, descriptor, history, allowHistory: true);

        Assert.Equal(3, chat.Plan!.Messages.Count);
        Assert.Equal("上一轮的问题", chat.Plan.Messages[0].Content);
    }

    [Fact]
    public async Task 超长输入直接报错而不是发出去()
    {
        var chat = new StubChatService();
        var runner = new PluginRunner(chat, new PluginPromptComposer());
        var descriptor = BuildDescriptor();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runner.RunComposerAsync(new PluginRunRequest(
                               descriptor,
                               descriptor.Mounts[0],
                               new Dictionary<string, string>(StringComparer.Ordinal),
                               new string('x', PluginRunner.MaxInputChars + 1),
                               Provider(),
                               "test-model",
                               new AgentRunMetrics())))
            {
            }
        });

        Assert.Contains("上限", error.Message);
        Assert.Null(chat.Plan);
    }

    private static async Task ConsumeAsync(
        PluginRunner runner,
        PluginDescriptor descriptor,
        IReadOnlyList<ChatMessage> history,
        bool allowHistory)
    {
        await foreach (var _ in runner.RunComposerAsync(new PluginRunRequest(
                           descriptor,
                           descriptor.Mounts[0],
                           new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" },
                           "hello",
                           Provider(),
                           "test-model",
                           new AgentRunMetrics(),
                           history)))
        {
            // 只关心发出去的 plan。
        }
    }

    private static ModelProvider Provider() => new()
    {
        Id = "test",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        ApiKey = "k"
    };

    private static PluginDescriptor BuildDescriptor(bool allowHistory = false)
    {
        var capability = new PluginCapability
        {
            Id = "translate",
            Name = "翻译",
            SkillPath = "skills/translate.md",
            SkillContent = """
                ---
                name: 翻译
                version: 1.0
                ---

                把文本翻译成 {{target.label}}（{{target}}）。
                """,
            AllowHistory = allowHistory,
            Parameters =
            [
                new PluginParameter
                {
                    Key = "target",
                    Label = "目标语言",
                    Kind = PluginParameterKind.Select,
                    Options = [new PluginParameterOption("ja", "日本語")]
                }
            ]
        };

        var mount = new PluginMount
        {
            PluginId = "translate",
            Id = "composer",
            Surface = PluginSurface.Composer,
            CapabilityId = "translate",
            Capability = capability
        };

        return new PluginDescriptor
        {
            Id = "translate",
            Name = "翻译",
            Source = PluginSource.Builtin,
            RootPath = Path.GetTempPath(),
            Capabilities = [capability],
            Mounts = [mount]
        };
    }

    /// <summary>记录收到的 plan，然后回一段固定文本。</summary>
    private sealed class StubChatService : IAgentChatService
    {
        public AgentRunPlan? Plan { get; private set; }

        public IAsyncEnumerable<AgentDelta> StreamAsync(
            ModelProvider provider,
            string modelId,
            IReadOnlyList<ChatMessage> history,
            IReadOnlySet<AgentSkill> skills,
            string? skillName = null,
            AgentRunMetrics? metrics = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("插件运行不应该走 StreamAsync。");

        public async IAsyncEnumerable<AgentDelta> RunAsync(
            AgentRunPlan plan,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Plan = plan;
            await Task.Yield();
            yield return new AgentDelta("译文", string.Empty);
        }
    }
}
