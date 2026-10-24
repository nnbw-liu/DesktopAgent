using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Agents;

/// <summary>
/// 内置工具循环（P1a）：取代 <c>FunctionInvokingChatClient</c>，由宿主自己跑
/// 「请求 → 工具 → 结果回灌 → 再请求」。
/// </summary>
/// <remarks>
/// <para>换掉 FICC 只为四件事，别的行为都照旧：</para>
/// <list type="number">
/// <item><b>step 边界</b>：第几次模型请求是一个事件，而不是宿主猜出来的。</item>
/// <item><b>工具历史保真</b>：下一步看到的是真实的 <c>tool_calls</c> + <c>tool</c> 消息，
/// 不是压成一段「工具轨迹」文本。</item>
/// <item><b>每步用量</b>：每次请求的 usage 各归各的。</item>
/// <item><b>本轮内换工具集</b>：工具表在循环里，补工具不需要等下一轮（P1b 使用）。</item>
/// </list>
/// <para>
/// 与 FICC 对齐的行为：最后一次请求摘掉工具（模型必须收尾成正文）；未知工具不执行、
/// 回一条错误结果让模型自己改；工具异常不炸整轮，回可读失败文本；同一响应里的多个
/// 工具调用按顺序串行执行（FICC 默认也是串行，避免并发写文件）。
/// </para>
/// </remarks>
internal static class NativeAgentLoop
{
    public static async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        IChatClient client,
        AgentInvocation invocation,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>(invocation.Messages);
        var tools = BuildToolMap(invocation.Tools);
        var maxSteps = Math.Max(1, invocation.MaxToolIterations);

        // P1b：本轮内补工具只补一次，补完后面几步都用扩充后的那张表。
        var activeOptions = options;
        var expanded = false;

        for (var step = 0; step < maxSteps; step++)
        {
            yield return new AgentStreamEvent.StepStarted(step, maxSteps, DateTimeOffset.Now);

            // 最后一次请求不再给工具：和 FICC 一样，逼模型把已有结果收尾成正文，
            // 否则小模型会在打满预算后继续要工具。
            var lastIteration = step == maxSteps - 1;
            var stepOptions = lastIteration ? WithoutTools(activeOptions) : activeOptions;

            var updates = new List<ChatResponseUpdate>();

            await foreach (var update in client
                               .GetStreamingResponseAsync(messages, stepOptions, cancellationToken)
                               .ConfigureAwait(false))
            {
                // 工具调用的参数是分片到达的，逐块读会拿到半截 JSON；先攒起来，流结束后整条取。
                updates.Add(update);

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case UsageContent { Details: { } usage }:
                            yield return new AgentStreamEvent.Usage(
                                (int)(usage.InputTokenCount ?? 0),
                                (int)(usage.OutputTokenCount ?? 0),
                                (int)(usage.CachedInputTokenCount ?? 0));
                            break;

                        case TextReasoningContent { Text: { Length: > 0 } chain }:
                            yield return new AgentStreamEvent.Reasoning(chain);
                            break;

                        case TextContent { Text: { Length: > 0 } chunk } when content is not TextReasoningContent:
                            yield return new AgentStreamEvent.Text(chunk);
                            break;
                    }
                }
            }

            var calls = new List<FunctionCallContent>();

            if (updates.Count > 0)
            {
                var response = updates.ToChatResponse();

                // 整条 assistant 消息（正文 + reasoning + tool_calls）原样进历史：
                // 下一步模型看到的是它自己说过的话，而不是宿主转述。
                messages.AddRange(response.Messages);

                calls.AddRange(response.Messages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionCallContent>()
                    // 服务端已经代跑过的调用（托管工具）只是告知，不能再执行一遍。
                    .Where(call => !call.InformationalOnly));
            }

            if (calls.Count == 0 || lastIteration)
            {
                // 没有工具调用 = 这一轮结束；最后一次仍要工具的（少数端点会这样）也直接收尾，
                // 否则会在没有工具声明的情况下继续发请求。
                yield return new AgentStreamEvent.StepCompleted(step, DateTimeOffset.Now);
                yield break;
            }

            foreach (var call in calls)
            {
                yield return new AgentStreamEvent.FunctionCall(call.CallId, call.Name, call.Arguments, step);

                var result = await InvokeAsync(call, tools, cancellationToken).ConfigureAwait(false);
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));

                yield return new AgentStreamEvent.FunctionResult(call.CallId, result, step);

                // P1b §3.2.4：模型自查到「本轮没给的工具有用」时，宿主在本轮内补一次并重发请求。
                // 条件是还有下一步可用（最后一步的工具面会被摘掉，补了也用不上）。
                if (expanded || lastIteration || invocation.ExpandTools is not { } expand)
                {
                    continue;
                }

                var added = expand(new AgentToolCallRequest(call.Name, call.Arguments, result, step));

                if (added.Count == 0)
                {
                    continue;
                }

                expanded = true;

                foreach (var tool in added)
                {
                    if (tool is AIFunction function)
                    {
                        tools[function.Name] = function;
                    }
                }

                activeOptions = WithAdditionalTools(activeOptions, added);

                yield return new AgentStreamEvent.ToolsExpanded(
                    step,
                    added.Select(tool => tool.Name).ToArray(),
                    "本轮补充工具：" + string.Join("、", added.Select(tool => tool.Name)));
            }

            yield return new AgentStreamEvent.StepCompleted(step, DateTimeOffset.Now);

            if (tools.Count == 0)
            {
                // 本轮一个工具都没声明（对话轮）：模型硬要调用只会得到「未提供」，
                // 再问下去也是同一个答案，收尾。
                yield break;
            }
        }
    }

    /// <summary>把工具按名字建表；FICC 也是大小写不敏感匹配。</summary>
    private static Dictionary<string, AIFunction> BuildToolMap(IReadOnlyList<AITool> tools)
    {
        var map = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
        {
            if (tool is AIFunction function)
            {
                map[function.Name] = function;
            }
        }

        return map;
    }

    /// <summary>
    /// 执行一次调用。失败一律变成「结果文本」而不是异常：模型要能看见失败原因并改法，
    /// 用户也不该因为一次坏参数丢掉整轮回复。
    /// </summary>
    private static async ValueTask<string> InvokeAsync(
        FunctionCallContent call,
        IReadOnlyDictionary<string, AIFunction> tools,
        CancellationToken cancellationToken)
    {
        if (!tools.TryGetValue(call.Name, out var function))
        {
            // 名字不在本轮曝光面里：不猜、不执行，回一句可操作的错误。
            return $"[TOOL_STATUS:error]\n本轮没有提供工具「{call.Name}」；"
                + "请只调用当前可见的工具，或者先用正文说明你打算做什么。";
        }

        try
        {
            var arguments = call.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(call.Arguments);

            var result = await function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

            return result switch
            {
                null => string.Empty,
                string text => text,
                _ => result.ToString() ?? string.Empty
            };
        }
        catch (OperationCanceledException)
        {
            // 用户停止 / 连接中断：这是控制流，不是工具失败。
            throw;
        }
        catch (Exception ex)
        {
            // 工具包装器已经处理了绝大多数异常；漏网之鱼给一句可读的话，别把堆栈灌进上下文。
            return $"[TOOL_STATUS:error]\n工具执行失败（未修改文件）：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>复制一份不带工具的选项（最后一次请求用）；其余设置原样保留。</summary>
    private static ChatOptions WithoutTools(ChatOptions options)
    {
        var clone = options.Clone();
        clone.Tools = null;
        clone.ToolMode = ChatToolMode.None;
        return clone;
    }

    /// <summary>复制一份在原工具集后追加了 <paramref name="added"/> 的选项（P1b 本轮补工具）。</summary>
    private static ChatOptions WithAdditionalTools(ChatOptions options, IReadOnlyList<AITool> added)
    {
        var clone = options.Clone();
        var tools = new List<AITool>(clone.Tools ?? []);
        tools.AddRange(added);
        clone.Tools = tools;
        clone.ToolMode ??= ChatToolMode.Auto;
        return clone;
    }
}
