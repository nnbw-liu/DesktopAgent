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
        private int _failedEdits;

        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            metrics?.RecordToolCall(inner.Name);

            var digest = string.Join(
                ";",
                arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));

            var guard = metrics?.Guard.Check(inner.Name, digest);

            if (guard is { Allowed: false })
            {
                return $"[TOOL_RESULT_BEGIN:{inner.Name}]\n{guard.Message}\n[TOOL_RESULT_END]";
            }

            // P6：小模型会反复提交同一种坏补丁。连续两次写不进去就禁用本工具，
            // 强迫它换策略（例如改用 write_file 整文件重写），而不是在同一处绕圈。
            if (_failedEdits >= 2 && IsEditTool(inner.Name))
            {
                var alternative = inner.Name switch
                {
                    "apply_patch" => "请改用 replace_text 精确替换，或 write_file 重写整个文件；也可以先用文本说明卡在哪里。",
                    "replace_text" => "请改用 apply_patch，或 write_file 重写整个文件；也可以先用文本说明卡在哪里。",
                    _ => "请改用 replace_text 或 apply_patch，或先用文本说明卡在哪里。"
                };
                return $"[TOOL_RESULT_BEGIN:{inner.Name}]\n"
                    + $"{inner.Name} 已在本次生成中连续失败 2 次，本轮禁用；{alternative}\n[TOOL_RESULT_END]";
            }

            string text;
            try
            {
                var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                text = result is string value ? value : result?.ToString() ?? string.Empty;
            }
            catch (ArgumentException ex)
            {
                // 小模型经常漏必填参数（例如只发 expectedHash 不发 patch）。
                // 直接抛异常只会得到 "Error: Function failed."，模型无法自我修复；
                // 这里把 schema 和收到的参数一起回传，让它按 schema 重试。
                var provided = string.Join(", ", arguments.Keys.OrderBy(key => key, StringComparer.Ordinal));
                text = $"{{\"error\":\"missing or invalid argument\",\"tool\":\"{inner.Name}\","
                    + $"\"provided\":\"{provided}\",\"message\":\"{Escape(ex.Message)}\","
                    + $"\"schema\":{inner.JsonSchema.GetRawText()}}}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                text = $"工具执行失败（未修改文件）：{ex.GetType().Name}: {ex.Message}";
            }

            if (guard?.Message is { Length: > 0 } warning)
            {
                text = $"[宿主提示] {warning}\n{text}";
            }

            if (IsEditTool(inner.Name)
                && (text.StartsWith("未写入任何文件", StringComparison.Ordinal)
                    || text.StartsWith("未替换任何内容", StringComparison.Ordinal)))
            {
                _failedEdits++;

                if (_failedEdits >= 2)
                {
                    text += "\n[宿主提示] 已连续失败 2 次。不要再调用本工具；请改用 replace_text 精确替换，或 write_file 重写整个文件。";
                }
            }
            else if (IsEditTool(inner.Name) && !text.StartsWith("未写入", StringComparison.Ordinal))
            {
                _failedEdits = 0;
            }

            return $"[TOOL_RESULT_BEGIN:{inner.Name}]\n{text}\n[TOOL_RESULT_END]";
        }

        private static bool IsEditTool(string name) =>
            name is "apply_patch" or "write_file" or "replace_text";

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
