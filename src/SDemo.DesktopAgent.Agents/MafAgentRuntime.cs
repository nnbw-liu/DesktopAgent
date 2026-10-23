using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace SDemo.DesktopAgent.Agents;

/// <summary>
/// 基于 Microsoft Agent Framework 的运行时实现。
/// 装配顺序刻意固定：<c>OpenAI ChatClient → Decorate（上下文裁剪层）→ FunctionInvokingChatClient → AsAIAgent</c>。
/// FICC 必须在装饰器外层，装饰器才能看到工具循环里的每一次请求（P1 §3.7 分支 A）。
/// </summary>
public sealed class MafAgentRuntime : IAgentRuntime
{
    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new OpenAIClientOptions { Endpoint = invocation.Endpoint };

        // 本地服务可能不需要 Key，但 OpenAI SDK 要求非空凭证。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(invocation.ApiKey) ? "not-required" : invocation.ApiKey);

        IChatClient client = new OpenAIClient(credential, options)
            .GetChatClient(invocation.ModelId)
            .AsIChatClient();

        if (invocation.Decorate is not null)
        {
            client = invocation.Decorate(client);
        }

        // P6：把工具循环显式收口。MAF 默认上限远大于应用的「每轮工具调用」预算；
        // 小模型被 Block 后仍会继续发工具调用，这里用请求轮数上限兜底。
        var invoking = new FunctionInvokingChatClient(client)
        {
            MaximumIterationsPerRequest = Math.Max(1, invocation.MaxToolIterations)
        };

        var agent = invoking.AsAIAgent(
            instructions: invocation.Instructions,
            name: invocation.AgentName,
            tools: invocation.Tools.Count == 0 ? null : invocation.Tools.ToList());

        await foreach (var update in agent
                           .RunStreamingAsync(invocation.Messages, cancellationToken: cancellationToken)
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
}
