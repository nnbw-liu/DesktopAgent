using System.Runtime.CompilerServices;
using SDemo.DesktopAgent.Context;
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

/// <summary>插件运行器：输入框的「发送」与气泡 / 划词的「派生卡片」两条路径。</summary>
public interface IPluginRunner
{
    /// <summary>输入框模式：按能力的 <c>allowHistory</c> 与上下文开关决定是否带历史。</summary>
    IAsyncEnumerable<AgentDelta> RunComposerAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 气泡 / 划词模式：输入始终是这条消息正文或选中文本；
    /// 历史默认不带，只有能力显式 <c>allowHistory</c> 且上下文开关打开时才带。
    /// </summary>
    IAsyncEnumerable<AgentDelta> RunArtifactAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件 Run 的编排：渲染 system 提示词、包装用户输入、组装 <see cref="AgentRunPlan"/>，
/// 然后交给 <see cref="IAgentChatService"/>。插件不注入全局技能，历史由
/// <c>allowHistory</c> + 可选的 <c>contextParameter</c> 共同决定。
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
    private readonly ContextBuilder _contextBuilder;

    public PluginRunner(
        IAgentChatService chat,
        PluginPromptComposer composer,
        ContextBuilder contextBuilder)
    {
        _chat = chat;
        _composer = composer;
        _contextBuilder = contextBuilder;
    }

    public IAsyncEnumerable<AgentDelta> RunComposerAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, cancellationToken);

    public IAsyncEnumerable<AgentDelta> RunArtifactAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, cancellationToken);

    private async IAsyncEnumerable<AgentDelta> RunCoreAsync(
        PluginRunRequest request,
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
        if (capability.AllowHistory
            && ShouldUseContext(capability, request.Values)
            && request.History is { Count: > 0 })
        {
            messages.AddRange(TakeHistory(request.History));
        }

        messages.Add(new ChatMessageModel(
            ChatRoleModel.User,
            _composer.WrapUserInput(request.InputText)));

        // 插件也走唯一的上下文入口：只是以 passthrough 模式跳过预算裁剪，
        // 输入上限仍由 MaxInputChars 单独把关。
        var context = _contextBuilder.Build(new ContextBuildRequest(
            request.Provider,
            request.ModelId,
            messages,
            InstructionsOverride: instructions,
            Passthrough: true));

        var plan = new AgentRunPlan(
            request.Provider,
            request.ModelId,
            context,
            NoTools,
            SessionId: string.Empty,
            request.Metrics);

        await foreach (var delta in _chat.RunAsync(plan, cancellationToken: cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// 「是否关联上下文」：能力没声明 contextParameter 时只看 allowHistory；
    /// 声明了则参数值为 false 的本轮不带历史（缺省 / 非法值按打开处理，避免静默丢上下文）。
    /// </summary>
    private static bool ShouldUseContext(
        PluginCapability capability,
        IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(capability.ContextParameter))
        {
            return true;
        }

        return !values.TryGetValue(capability.ContextParameter, out var value)
            || !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>最近 10 条、总字符 ≤ 24,000（§9.3）。</summary>
    private static IReadOnlyList<ChatMessageModel> TakeHistory(IReadOnlyList<ChatMessageModel> history)
    {
        var selected = new List<ChatMessageModel>();
        var characters = 0;

        for (var index = history.Count - 1; index >= 0 && selected.Count < HistoryMaxMessages; index--)
        {
            var message = history[index];
            var length = ContextCharCount(message);
            if (length == 0)
            {
                continue;
            }

            if (characters + length > HistoryMaxChars)
            {
                break;
            }

            characters += length;
            selected.Add(message);
        }

        selected.Reverse();
        return selected;
    }

    /// <summary>这条消息实际会进入上下文的字符数：正文 + 已「加入上下文」的派生卡片。</summary>
    private static int ContextCharCount(ChatMessageModel message)
    {
        var length = message.Content.Length;
        foreach (var artifact in message.Derived)
        {
            if (artifact.ContributesToContext)
            {
                length += artifact.Text.Length;
            }
        }

        return length;
    }
}
