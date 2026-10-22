using System.Runtime.CompilerServices;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatRoleModel = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>一次插件 Run 的输入：谁、用哪些参数、处理什么文本、走哪个模型。</summary>
public sealed record PluginRunRequest(
    PluginDescriptor Plugin,
    PluginMount Mount,
    IReadOnlyDictionary<string, string> Values,
    string InputText,
    ModelProvider Provider,
    string ModelId,
    AgentRunMetrics Metrics,
    IReadOnlyList<ChatMessageModel>? History = null);

/// <summary>插件运行器：输入框的「发送」与气泡的「派生卡片」两条路径。</summary>
public interface IPluginRunner
{
    /// <summary>输入框模式：按能力的 <c>allowHistory</c> 决定是否带历史。</summary>
    IAsyncEnumerable<AgentDelta> RunComposerAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>气泡模式：只加工这条消息本身，<b>永远不带历史</b>。</summary>
    IAsyncEnumerable<AgentDelta> RunArtifactAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件 Run 的编排：渲染 system 提示词、包装用户输入、组装 <see cref="AgentRunPlan"/>，
/// 然后交给 <see cref="IAgentChatService"/>。插件不注入全局技能、不带历史，除非能力显式打开。
/// </summary>
public sealed class PluginRunner : IPluginRunner
{
    /// <summary>单次输入上限；超出需要分段（§9.4，尚未实现）。</summary>
    public const int MaxInputChars = 60_000;

    private const int HistoryMaxMessages = 10;
    private const int HistoryMaxChars = 24_000;

    // v1 的声明式插件不支持工具。
    private static readonly HashSet<string> NoTools = [];

    private readonly IAgentChatService _chat;
    private readonly PluginPromptComposer _composer;

    public PluginRunner(IAgentChatService chat, PluginPromptComposer composer)
    {
        _chat = chat;
        _composer = composer;
    }

    public IAsyncEnumerable<AgentDelta> RunComposerAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, useHistory: true, cancellationToken);

    public IAsyncEnumerable<AgentDelta> RunArtifactAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, useHistory: false, cancellationToken);

    private async IAsyncEnumerable<AgentDelta> RunCoreAsync(
        PluginRunRequest request,
        bool useHistory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var capability = request.Mount.Capability
            ?? throw new InvalidOperationException($"入口 {request.Mount.Key} 没有解析出能力，无法执行。");

        if (request.InputText.Length > MaxInputChars)
        {
            throw new InvalidOperationException(
                $"待处理文本 {request.InputText.Length:N0} 字符，超过 {MaxInputChars:N0} 字符上限（长文本分段尚未实现）。");
        }

        var instructions = _composer.BuildSystemPrompt(capability, request.Values);

        var messages = new List<ChatMessageModel>();
        if (useHistory && capability.AllowHistory && request.History is { Count: > 0 })
        {
            messages.AddRange(TakeHistory(request.History));
        }

        messages.Add(new ChatMessageModel(
            ChatRoleModel.User,
            _composer.WrapUserInput(request.InputText)));

        var plan = new AgentRunPlan(
            request.Provider,
            request.ModelId,
            instructions,
            messages,
            NoTools,
            SessionId: string.Empty,
            request.Metrics);

        await foreach (var delta in _chat.RunAsync(plan, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>最近 10 条、总字符 ≤ 24,000（§9.3）。</summary>
    private static IReadOnlyList<ChatMessageModel> TakeHistory(IReadOnlyList<ChatMessageModel> history)
    {
        var selected = new List<ChatMessageModel>();
        var characters = 0;

        for (var index = history.Count - 1; index >= 0 && selected.Count < HistoryMaxMessages; index--)
        {
            var message = history[index];
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (characters + message.Content.Length > HistoryMaxChars)
            {
                break;
            }

            characters += message.Content.Length;
            selected.Add(message);
        }

        selected.Reverse();
        return selected;
    }
}
