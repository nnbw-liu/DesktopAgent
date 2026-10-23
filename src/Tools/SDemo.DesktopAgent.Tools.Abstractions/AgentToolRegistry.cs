using System.Text.Json;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 工具包注册表：唯一决定“哪些包在当前平台可见、本轮哪些包可以交给模型”。
/// 平台过滤（<see cref="AgentToolPackDescriptor.WindowsOnly"/>）在这里做，
/// 所以 Android 即使误注册了 PowerShell 包也不会暴露给模型。
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly IReadOnlyList<IAgentToolPack> _packs;
    private readonly ISkillLibrary? _skills;

    public AgentToolRegistry(IEnumerable<IAgentToolPack> packs, ISkillLibrary? skills = null)
    {
        _packs = packs.ToArray();
        _skills = skills;
    }

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
    /// 某个启用集合下、当前平台可用的函数名（按工具包 Order 排列）。
    /// 路由只需要名字，不需要实例化工具，所以它比 <see cref="Create"/> 便宜得多。
    /// </summary>
    public IReadOnlyList<string> ToolNames(IReadOnlySet<string> enabledPackIds) =>
        EnabledPacks(enabledPackIds)
            .SelectMany(pack => pack.Descriptor.Functions.Select(function => function.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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

        foreach (var pack in EnabledPacks(enabledPackIds))
        {
            foreach (var tool in pack.Create(context))
            {
                tools.Add(Instrument(tool, metrics));
            }
        }

        return tools;
    }

    /// <summary>
    /// M1：只生成路由选中的工具（核心集 + 选中包），并可附加 <c>search_capabilities</c> 兜底入口。
    /// 仍然只从「平台支持 ∩ 用户启用」的包里生成，路由结果不会绕过开关。
    /// </summary>
    public IReadOnlyList<AITool> CreateExposed(
        ToolContext context,
        IReadOnlySet<string> enabledPackIds,
        IReadOnlySet<string> exposedToolNames,
        AgentRunMetrics? metrics = null,
        bool escapeHatch = false)
    {
        var tools = Create(context, enabledPackIds, metrics)
            .Where(tool => exposedToolNames.Contains(tool.Name, StringComparer.Ordinal))
            .ToList();

        if (escapeHatch && ToolNames(enabledPackIds).Count > 0)
        {
            tools.Add(Instrument(CreateEscapeHatch(enabledPackIds, exposedToolNames), metrics));
        }

        return tools;
    }

    private IEnumerable<IAgentToolPack> EnabledPacks(IReadOnlySet<string> enabledPackIds) =>
        _packs
            .Where(pack => IsPlatformSupported(pack.Descriptor))
            .Where(pack => enabledPackIds.Contains(pack.Descriptor.Id))
            .OrderBy(pack => pack.Descriptor.Order)
            .ThenBy(pack => pack.Descriptor.Id, StringComparer.Ordinal);

    /// <summary>
    /// 兜底入口（§4.4 escape hatch）：让模型在路由漏掉工具时按关键词自查可用能力。
    /// 它不绕过用户开关：返回的永远是「平台支持 ∩ 用户启用」里的工具。
    /// </summary>
    private AITool CreateEscapeHatch(
        IReadOnlySet<string> enabledPackIds,
        IReadOnlySet<string> exposedToolNames)
    {
        var entries = EnabledPacks(enabledPackIds)
            .SelectMany(pack => pack.Descriptor.Functions
                .Select(function => (Pack: pack.Descriptor, Function: function)))
            .ToArray();

        return AIFunctionFactory.Create(
            (string query) => SearchCapabilities(entries, exposedToolNames, query),
            name: CapabilityRouter.EscapeHatchTool,
            description: "按关键词查找当前会话可用的工具与技能；返回名字、所属分组与用途。");
    }

    private string SearchCapabilities(
        IReadOnlyList<(AgentToolPackDescriptor Pack, AgentToolFunctionDescriptor Function)> entries,
        IReadOnlySet<string> exposedToolNames,
        string query)
    {
        var normalized = (query ?? string.Empty).Trim().ToLowerInvariant();

        var matches = entries
            .Where(entry => normalized.Length == 0 || Matches(entry, normalized))
            .Take(20)
            .ToArray();

        // 中文整句匹配不到时退回「全部可用工具」，总比一句“没找到”更有用。
        var fallback = matches.Length == 0;
        if (fallback)
        {
            matches = entries.Take(20).ToArray();
        }

        var lines = new List<string>
        {
            normalized.Length == 0
                ? "当前会话可用工具："
                : fallback
                    ? $"没有精确匹配「{normalized}」，以下是全部可用工具："
                    : $"匹配「{normalized}」的工具："
        };

        foreach (var (pack, function) in matches)
        {
            var state = exposedToolNames.Contains(function.Id, StringComparer.Ordinal) ? "已暴露" : "本轮未暴露";
            lines.Add($"- {function.Id}（{pack.Name}）：{pack.Description} [{state}]");
        }

        lines.Add("提示：本轮未暴露的工具不会交给模型；如需使用，请在下一轮明确说明用途，宿主会重新路由。");

        AppendSkills(lines, normalized);

        return string.Join('\n', lines);
    }

    /// <summary>M2：兜底入口也列出技能索引，模型可以直接知道该 load_skill 哪个。</summary>
    private void AppendSkills(List<string> lines, string normalized)
    {
        var skills = _skills?.Index() ?? [];
        if (skills.Count == 0)
        {
            return;
        }

        var matched = normalized.Length == 0
            ? skills
            : skills.Where(skill =>
                skill.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || skill.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || skill.WhenToUse.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || skill.Tags.Any(tag => tag.Contains(normalized, StringComparison.OrdinalIgnoreCase))).ToArray();

        lines.Add(matched.Count > 0
            ? $"\n可用技能（用 load_skill(name) 读取正文，共 {skills.Count} 个）："
            : $"\n没有匹配「{normalized}」的技能；当前共 {skills.Count} 个：");

        foreach (var skill in (matched.Count > 0 ? matched : skills.Take(5)).Take(5))
        {
            lines.Add($"- {skill.Name}：{skill.Description}");
        }
    }

    private static bool Matches(
        (AgentToolPackDescriptor Pack, AgentToolFunctionDescriptor Function) entry,
        string normalized) =>
        entry.Function.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase)
        || entry.Function.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
        || entry.Pack.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase)
        || entry.Pack.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
        || entry.Pack.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase);

    /// <summary>工具失败 / 被拒绝时写在结果首行的状态标记；成功时不写，避免占 token。</summary>
    internal const string ToolStatusPrefix = "[TOOL_STATUS:";

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
                metrics?.RecordToolResult(inner.Name, ToolTraceEntry.StatusRejected);
                return Envelope(inner.Name, guard.Message ?? "本轮工具调用已达上限。", ToolTraceEntry.StatusRejected);
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
                metrics?.RecordToolResult(inner.Name, ToolTraceEntry.StatusRejected);
                return Envelope(
                    inner.Name,
                    $"{inner.Name} 已在本次生成中连续失败 2 次，本轮禁用；{alternative}",
                    ToolTraceEntry.StatusRejected);
            }

            string text;
            var status = ToolTraceEntry.StatusOk;
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
                status = ToolTraceEntry.StatusError;
                var provided = string.Join(", ", arguments.Keys.OrderBy(key => key, StringComparer.Ordinal));
                text = $"{{\"error\":\"missing or invalid argument\",\"tool\":\"{inner.Name}\","
                    + $"\"provided\":\"{provided}\",\"message\":\"{Escape(ex.Message)}\","
                    + $"\"schema\":{inner.JsonSchema.GetRawText()}}}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = ToolTraceEntry.StatusError;
                text = $"工具执行失败（未修改文件）：{ex.GetType().Name}: {ex.Message}";
            }

            if (guard?.Message is { Length: > 0 } warning)
            {
                text = $"[宿主提示] {warning}\n{text}";
            }

            if (IsEditTool(inner.Name))
            {
                if (IsEditFailure(text))
                {
                    status = ToolTraceEntry.StatusRejected;
                    _failedEdits++;

                    if (_failedEdits >= 2)
                    {
                        text += "\n[宿主提示] 已连续失败 2 次。不要再调用本工具；请改用 replace_text 精确替换，或 write_file 重写整个文件。";
                    }
                }
                else if (status == ToolTraceEntry.StatusOk)
                {
                    _failedEdits = 0;
                }
            }

            if (status == ToolTraceEntry.StatusOk)
            {
                status = InferStatus(text);
            }

            metrics?.RecordToolResult(inner.Name, status);

            return Envelope(inner.Name, text, status);
        }

        /// <summary>编辑类工具的“没写进去”结果；这些不算成功，避免循环保护与统计误判。</summary>
        private static bool IsEditFailure(string text) =>
            text.Contains("未写入任何文件", StringComparison.Ordinal)
            || text.Contains("未替换任何内容", StringComparison.Ordinal)
            || text.Contains("写入失败", StringComparison.Ordinal)
            || text.Contains("创建检查点失败", StringComparison.Ordinal)
            || text.Contains("疑似整文件重写丢失内容", StringComparison.Ordinal);

        /// <summary>非编辑工具的错误文本也要有状态，便于 trace / 评测区分“没结果”和“执行失败”。</summary>
        private static string InferStatus(string text)
        {
            if (text.Contains("用户拒绝", StringComparison.Ordinal)
                || text.Contains("用户取消了", StringComparison.Ordinal))
            {
                return ToolTraceEntry.StatusRejected;
            }

            if (text.StartsWith("工具执行失败", StringComparison.Ordinal)
                || text.StartsWith("执行失败", StringComparison.Ordinal)
                || text.StartsWith("错误", StringComparison.Ordinal)
                || text.StartsWith("正则无效", StringComparison.Ordinal)
                || text.StartsWith("搜索超时", StringComparison.Ordinal)
                || text.StartsWith("{\"error\"", StringComparison.Ordinal))
            {
                return ToolTraceEntry.StatusError;
            }

            return ToolTraceEntry.StatusOk;
        }

        private static string Envelope(string toolName, string text, string status)
        {
            var marker = string.Equals(status, ToolTraceEntry.StatusOk, StringComparison.Ordinal)
                ? string.Empty
                : $"{ToolStatusPrefix}{status}]\n";

            return $"[TOOL_RESULT_BEGIN:{toolName}]\n{marker}{text}\n[TOOL_RESULT_END]";
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
