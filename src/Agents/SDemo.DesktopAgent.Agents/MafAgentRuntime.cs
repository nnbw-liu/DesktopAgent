using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace SDemo.DesktopAgent.Agents;

/// <summary>
/// 基于 Microsoft Agent Framework 的运行时实现。
/// 装配顺序刻意固定：<c>OpenAI ChatClient → Decorate（上下文裁剪层）→ 工具循环</c>。
/// 循环必须在装饰器外层，装饰器才能看到工具循环里的每一次请求（P1 §3.7 分支 A）。
/// </summary>
/// <remarks>
/// 流式请求直接走 <see cref="IChatClient.GetStreamingResponseAsync"/>：
/// MAF 的 <c>AIAgent</c> 包装层会把 <c>ChatOptions</c>（推理强度）挡在外面，也拿不到
/// <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/> 这类过程内容，
/// 界面上就看不到「调用了什么工具」。
/// <para>
/// 工具循环有两种：默认是 MAF 的 <see cref="FunctionInvokingChatClient"/>；
/// <see cref="AgentInvocation.NativeLoop"/> 为真时走 <see cref="NativeAgentLoop"/>（P1a）。
/// 两者共用同一条客户端链与同一份 <see cref="ChatOptions"/>，所以裁剪、思考强度、工具面都不变。
/// </para>
/// </remarks>
public sealed class MafAgentRuntime : IAgentRuntime
{
    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = CreateClient(invocation);
        var chatOptions = CreateOptions(invocation);

        if (invocation.NativeLoop)
        {
            await foreach (var evt in NativeAgentLoop
                               .StreamAsync(client, invocation, chatOptions, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return evt;
            }

            yield break;
        }

        // P6：把工具循环显式收口。MAF 默认上限远大于应用的「每轮工具调用」预算；
        // 小模型被 Block 后仍会继续发工具调用，这里用请求轮数上限兜底。
        var invoking = new FunctionInvokingChatClient(client)
        {
            MaximumIterationsPerRequest = Math.Max(1, invocation.MaxToolIterations)
        };

        await foreach (var update in invoking
                           .GetStreamingResponseAsync(invocation.Messages, chatOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case UsageContent { Details: { } usage }:
                        yield return new AgentStreamEvent.Usage(
                            (int)(usage.InputTokenCount ?? 0),
                            (int)(usage.OutputTokenCount ?? 0));
                        break;

                    case TextReasoningContent { Text: { Length: > 0 } chain }:
                        yield return new AgentStreamEvent.Reasoning(chain);
                        break;

                    case TextContent { Text: { Length: > 0 } chunk } when content is not TextReasoningContent:
                        yield return new AgentStreamEvent.Text(chunk);
                        break;

                    case FunctionCallContent call:
                        yield return new AgentStreamEvent.FunctionCall(call.CallId, call.Name, call.Arguments);
                        break;

                    case FunctionResultContent result:
                        yield return new AgentStreamEvent.FunctionResult(result.CallId, result.Result?.ToString());
                        break;
                }
            }
        }
    }

    /// <summary>OpenAI 兼容客户端，再套上调用方给的装饰器（上下文裁剪 / 计量）。</summary>
    private static IChatClient CreateClient(AgentInvocation invocation)
    {
        var options = new OpenAIClientOptions { Endpoint = invocation.Endpoint };

        // 本地服务可能不需要 Key，但 OpenAI SDK 要求非空凭证。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(invocation.ApiKey) ? "not-required" : invocation.ApiKey);

        IChatClient client = new OpenAIClient(credential, options)
            .GetChatClient(invocation.ModelId)
            .AsIChatClient();

        return invocation.Decorate is null ? client : invocation.Decorate(client);
    }

    private static ChatOptions CreateOptions(AgentInvocation invocation)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = invocation.Instructions,
            Tools = invocation.Tools.Count == 0 ? null : [.. invocation.Tools]
        };

        if (ParseReasoningEffort(invocation.ReasoningEffort) is { } effort)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };
        }

        return chatOptions;
    }

    /// <summary>
    /// 把设置里的「思考强度」映射成 MEAI 的枚举；空 / 未知值表示不显式请求（用服务端默认）。
    /// <c>none</c> 是显式关闭：不发这个字段时，端点默认值说了算（Qwen3 / Ollama 等默认就是思考），
    /// 「不思考」只有显式传 <c>reasoning_effort: none</c> 才真的关得掉。
    /// </summary>
    internal static ReasoningEffort? ParseReasoningEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        _ => null
    };
}
