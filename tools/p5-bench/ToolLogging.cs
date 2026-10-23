using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Services;

namespace SDemo.P5Bench;

/// <summary>
/// 评测期工具日志：把每次工具调用的参数与结果原文写进文件，供失败复盘。
/// 只包在 p5-bench 里，产品代码不引用。
/// </summary>
internal sealed class LoggingToolPack(IAgentToolPack inner, string logPath) : IAgentToolPack
{
    public AgentToolPackDescriptor Descriptor => inner.Descriptor;

    public IReadOnlyList<AITool> Create(ToolContext context) =>
        inner.Create(context)
            .Select(tool => tool is AIFunction function ? new LoggingFunction(function, logPath) : tool)
            .ToArray();
}

internal sealed class LoggingFunction(AIFunction inner, string logPath) : AIFunction
{
    private static readonly object Gate = new();

    public override string Name => inner.Name;

    public override string Description => inner.Description;

    public override JsonElement JsonSchema => inner.JsonSchema;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var args = string.Join(
            ", ",
            arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        try
        {
            var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            Append($"[{Name}] ARGS: {args}\n[{Name}] RESULT: {result}\n");
            return result;
        }
        catch (Exception ex)
        {
            Append($"[{Name}] ARGS: {args}\n[{Name}] THROW: {ex}\n");
            throw;
        }
    }

    private void Append(string text)
    {
        lock (Gate)
        {
            File.AppendAllText(logPath, text + "\n---\n", new UTF8Encoding(false));
        }
    }
}
