using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 工具包注册表：唯一决定“哪些包在当前平台可见、本轮哪些包可以交给模型”。
/// 平台过滤（<see cref="AgentToolPackDescriptor.WindowsOnly"/>）在这里做，
/// 所以 Android 即使误注册了 PowerShell 包也不会暴露给模型。
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly IReadOnlyList<IAgentToolPack> _packs;

    public AgentToolRegistry(IEnumerable<IAgentToolPack> packs) => _packs = packs.ToArray();

    /// <summary>当前平台可见的工具包（按 Order 排序）。</summary>
    public IReadOnlyList<AgentToolPackDescriptor> Descriptors =>
        _packs
            .Select(pack => pack.Descriptor)
            .Where(IsPlatformSupported)
            .OrderBy(descriptor => descriptor.Order)
            .ThenBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();

    public AgentToolPackDescriptor? Find(string packId) =>
        Descriptors.FirstOrDefault(descriptor => string.Equals(descriptor.Id, packId, StringComparison.Ordinal));

    /// <summary>某个包在当前平台是否可用（WindowsOnly 包在非 Windows 上永远不可用）。</summary>
    public static bool IsPlatformSupported(AgentToolPackDescriptor descriptor) =>
        !descriptor.WindowsOnly || OperatingSystem.IsWindows();

    /// <summary>
    /// 按本轮启用的包生成工具。没有启用的包、平台不支持的包都不会出现在结果里，
    /// 模型看不到也就调用不到。
    /// </summary>
    public IReadOnlyList<AITool> Create(
        ToolContext context,
        IReadOnlySet<string> enabledPackIds,
        AgentRunMetrics? metrics = null)
    {
        var tools = new List<AITool>();

        foreach (var pack in _packs
                     .Where(pack => IsPlatformSupported(pack.Descriptor))
                     .Where(pack => enabledPackIds.Contains(pack.Descriptor.Id))
                     .OrderBy(pack => pack.Descriptor.Order))
        {
            foreach (var tool in pack.Create(context))
            {
                tools.Add(Instrument(tool, metrics));
            }
        }

        return tools;
    }

    /// <summary>
    /// 记录调用次数，并把文本结果包进固定标记：模型必须把标记内的内容当数据，而不是指令。
    /// </summary>
    private static AITool Instrument(AITool tool, AgentRunMetrics? metrics) =>
        tool is not AIFunction function ? tool : new InstrumentedAIFunction(function, metrics);

    private sealed class InstrumentedAIFunction(AIFunction inner, AgentRunMetrics? metrics) : AIFunction
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            metrics?.RecordToolCall(inner.Name);

            var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

            // 工具结果定界：内容里即使出现像指令的句子，也只能落在标记内部。
            return result is string text
                ? $"[TOOL_RESULT_BEGIN:{inner.Name}]\n{text}\n[TOOL_RESULT_END]"
                : result;
        }
    }
}
