using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>一轮路由的输入（§4.2 / §4.3）：用户原句、最近轨迹、可用工具、模式与上限。</summary>
/// <param name="PreviousUserInputs">
/// 本轮之前的用户输入（新的在前，最多几条）。用来在「这一句没有任务信号」时沿用上一轮的
/// 任务档（P1b §3.2.3）——「继续」不该让工具面跳变。为空时行为与只做单轮判定完全一致。
/// </param>
public sealed record CapabilityRoutingInput(
    string UserInput,
    IReadOnlyList<string> RecentToolIds,
    IReadOnlyList<string> AvailableTools,
    AgentMode Mode,
    int MaxExposedTools,
    bool EscapeHatch = true,
    bool ExposeAll = false,
    bool HasSelectedSkill = false,
    IReadOnlyList<string>? PreviousUserInputs = null);

/// <summary>
/// 能力路由：成本档 0 的规则实现（M1–M3），外加成本档 1 的小模型建议落地（M4）。
/// 规则结果永远是兜底；<see cref="ApplyAdvice"/> 只会在「平台支持 ∩ 用户启用」的集合里选，
/// 且仍受模式 / 窗口上限约束，模型不能凭路由结果绕过开关。
/// 一轮只路由一次；Compact 只用规则。
/// </summary>
public static class CapabilityRouter
{
    /// <summary>Compact 模式的上限，沿用 P6 的实测结论。</summary>
    public const int CompactMaxTools = 4;

    /// <summary>路由失败时的兜底入口：列出当前会话可用的工具。</summary>
    public const string EscapeHatchTool = "search_capabilities";

    /// <summary>技能正文加载（M2）：系统提示只放索引，正文按需读取。</summary>
    public const string LoadSkillTool = "load_skill";

    /// <summary>工作区知识库检索（M3）。</summary>
    public const string SearchMemoryTool = "search_workspace_memory";

    /// <summary>工作区知识库正文读取（M3）。</summary>
    public const string ReadMemoryTool = "read_archived_session";

    /// <summary>
    /// 核心集（§4.4）：只读、定位、修改、执行、计划各一个代表，外加两个元工具。
    /// 只有在可用集合里出现时才会暴露，用户关掉某个包不会凭空多出工具。
    /// </summary>
    public static readonly IReadOnlyList<string> CoreTools =
        ["read_file", "search_files", "apply_patch", "run_command", "todo_write", LoadSkillTool];

    private static readonly string[] EditWords =
    [
        "改", "修", "加", "重构", "实现", "新增", "新建", "替换", "删除", "测试",
        "fix", "add", "implement", "refactor", "replace"
    ];

    private static readonly string[] ReadWords =
        ["查", "解释", "在哪", "为什么", "怎么", "目录结构", "看目录", "what", "where", "why", "explain", "find"];

    private static readonly string[] RunWords =
        ["跑", "构建", "测试", "编译", "build", "test", "run", "compile"];

    private static readonly string[] PdfWords = ["pdf", "扫描件", "文档页"];

    private static readonly string[] ShellWords = ["powershell", "脚本", "沙箱"];

    private static readonly string[] WebWords = ["联网", "网上", "网页", "web", "搜索一下"];

    private static readonly string[] TodoWords = ["计划", "待办", "步骤", "todo"];

    /// <summary>
    /// 跨会话回忆（M3）。必须排在 shell 之前：验收句「上次关于沙箱 ACL 的结论」
    /// 里的「沙箱」不能把路由带去 run_powershell。
    /// </summary>
    private static readonly string[] MemoryWords =
    [
        "上次", "之前", "以前", "归档", "历史会话", "记得", "结论", "上次讨论", "之前说",
        "last time", "previous", "earlier", "discussed"
    ];

    /// <summary>
    /// M4：规则看不出任务信号时不值得花一次模型调用（「嗯」「你好」直接走规则）。
    /// 这里只做粗筛，真正的选择由小模型完成。
    /// </summary>
    private static readonly string[] TaskSignalWords =
    [
        "帮", "请", "把", "给我", "看看", "检查", "分析", "写", "生成", "整理", "导出", "翻译", "总结",
        "查找", "定位", "修改", "调整", "对比", "优化", "清理", "安装", "配置", "部署", "运行", "测试",
        "文件", "代码", "项目", "目录", "报错", "错误", "日志", "功能", "需求", "怎么做", "如何", "为什么",
        "能", "可以", "需要", "支持", "文档", "网页", "脚本", "命令",
        "help", "please", "write", "create", "update", "change", "check", "find", "search", "build",
        "test", "run", "fix", "review", "explain", "file", "code", "project", "error", "log"
    ];

    private static readonly (string Intent, string[] Words, string[] Tools)[] IntentRules =
    [
        // M3：跨会话回忆排在 edit / shell 之前，避免「上次关于沙箱 ACL 的结论」被当成执行脚本。
        ("memory", MemoryWords, [SearchMemoryTool, ReadMemoryTool]),
        // 小模型优先用「精确替换」；apply_patch 失败后还有 write_file 兜底。
        ("edit", EditWords, ["read_file", "replace_text", "apply_patch", "write_file"]),
        ("pdf", PdfWords, ["pdf_read", "pdf_info", "read_file"]),
        ("shell", ShellWords, ["run_powershell", "read_file"]),
        ("run", RunWords, ["run_command", "read_file"]),
        ("web", WebWords, ["web_search"]),
        ("plan", TodoWords, ["todo_write"]),
        ("read", ReadWords, ["search_files", "read_file", "list_tree"])
    ];

    /// <summary>
    /// 按规则选出一轮暴露的工具；结果永远在 <paramref name="input"/> 的可用集合内。
    /// </summary>
    public static RoutingDecision Route(CapabilityRoutingInput input)
    {
        var available = Normalize(input.AvailableTools);
        var intentKind = TurnClassifier.Classify(input.UserInput, input.HasSelectedSkill);

        // 先决定「做什么」：纯对话轮不暴露任何工具（全量暴露是排障逃生口，优先于该判断）。
        if (intentKind == TurnIntent.Chat && !input.ExposeAll)
        {
            return new RoutingDecision(
                [],
                [],
                [],
                MaxExposedTools: 0,
                EstimatedSchemaTokens: 0,
                EscapeHatch: false,
                Truncated: false,
                Reason: $"{input.Mode} · 意图=chat · 对话轮不调用工具")
            {
                Source = RoutingDecision.RulesSource,
                Intent = TurnIntent.Chat
            };
        }

        if (available.Length == 0)
        {
            return RoutingDecision.Empty with
            {
                MaxExposedTools = Math.Max(0, input.MaxExposedTools),
                Reason = "没有可用工具",
                Intent = intentKind
            };
        }

        var (intent, intentTools) = DetectIntent(input.UserInput);

        // P1b §3.2.3：先定任务档，再补本句意图。档内工具集固定，相邻轮只增不减——
        // 这一句没有任务信号时沿用上一轮的档，而不是让工具面随关键词抖动。
        var (profile, inherited) = ResolveProfile(input);
        var profileTools = ToolsOfProfile(profile);
        var ordered = new List<string>();

        if (input.Mode == AgentMode.Compact)
        {
            // Compact 保持 P6 行为：任务档优先，本句意图补包，最近用过的工具插到最前面，
            // 末位兜底四件套。
            AddCandidates(ordered, profileTools, available);
            AddCandidates(ordered, intentTools, available);

            foreach (var recent in input.RecentToolIds.Reverse())
            {
                if (Matches(recent, available) && !ordered.Contains(recent, StringComparer.Ordinal))
                {
                    ordered.Insert(0, recent);
                }
            }

            AddCandidates(ordered, CompactFallback, available);
        }
        else
        {
            // Full：核心集先来，再按任务档与意图补包，最后接最近用过的工具。
            // 不再用「全部可用工具」填满剩余名额：名额是上限，不是指标。
            AddCandidates(ordered, CoreTools, available);
            AddCandidates(ordered, profileTools, available);
            AddCandidates(ordered, intentTools, available);

            foreach (var recent in input.RecentToolIds.Reverse())
            {
                if (Matches(recent, available) && !ordered.Contains(recent, StringComparer.Ordinal))
                {
                    ordered.Add(recent);
                }
            }
        }

        return Finalize(
            input,
            available,
            ordered,
            intent,
            RoutingDecision.RulesSource,
            advice: null,
            intentKind,
            profile,
            inherited);
    }

    /// <summary>
    /// M4：把模型建议落成 <see cref="RoutingDecision"/>。建议里的未知工具会被丢弃，
    /// 元工具（兜底入口 / load_skill）仍由宿主按模式追加；建议不足时用规则结果补满。
    /// 建议为空 / 全部非法 / 全量暴露时直接返回 <paramref name="fallback"/>。
    /// </summary>
    public static RoutingDecision ApplyAdvice(
        CapabilityRoutingInput input,
        RoutingAdvice advice,
        RoutingDecision fallback)
    {
        if (input.ExposeAll || advice.Tools.Count == 0)
        {
            return fallback;
        }

        // 对话轮已经判定「不需要工具」，小模型建议不能把它改回工具轮。
        if (fallback.Intent == TurnIntent.Chat)
        {
            return fallback;
        }

        var available = Normalize(input.AvailableTools);
        if (available.Length == 0)
        {
            return fallback;
        }

        var canonical = available.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var requested in advice.Tools)
        {
            if (!canonical.TryGetValue(requested.Trim(), out var name)
                || string.Equals(name, LoadSkillTool, StringComparison.Ordinal)
                || string.Equals(name, EscapeHatchTool, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ordered.Contains(name, StringComparer.Ordinal))
            {
                ordered.Add(name);
            }
        }

        if (ordered.Count == 0)
        {
            return fallback with
            {
                Reason = fallback.Reason + " · 小模型建议没有可用工具，沿用规则"
            };
        }

        // 建议只决定「优先顺序」；规则结果（核心集 / 最近工具）补齐建议漏掉的部分；
        // 最终仍由 Finalize 按上限截断，不再用全部可用工具填满。
        foreach (var name in fallback.ExposedTools)
        {
            if (!string.Equals(name, LoadSkillTool, StringComparison.Ordinal)
                && !string.Equals(name, EscapeHatchTool, StringComparison.Ordinal)
                && !ordered.Contains(name, StringComparer.Ordinal)
                && Matches(name, available))
            {
                ordered.Add(name);
            }
        }

        var intent = string.IsNullOrWhiteSpace(advice.Intent) ? "unknown" : advice.Intent.Trim();
        var (profile, inherited) = ResolveProfile(input);

        return Finalize(
            input,
            available,
            ordered,
            intent,
            RoutingDecision.SmallModelSource,
            advice,
            TurnClassifier.Classify(input.UserInput, input.HasSelectedSkill),
            profile,
            inherited);
    }

    /// <summary>
    /// 任务档（P1b §3.2.3）：本句有任务信号用它，否则沿用最近一条有任务信号的历史输入。
    /// 判不出来时是 <see cref="ToolTaskProfile.General"/>（只给核心集，与 M4 之前一致）。
    /// </summary>
    public static ToolTaskProfile ResolveProfile(string? userInput, IReadOnlyList<string>? previousUserInputs = null)
        => ResolveProfile(new CapabilityRoutingInput(
            userInput ?? string.Empty,
            [],
            [],
            AgentMode.Full,
            MaxExposedTools: 0,
            PreviousUserInputs: previousUserInputs)).Profile;

    /// <summary>同上；<paramref name="inherited"/> 表示档是从历史轮沿用来的。</summary>
    public static (ToolTaskProfile Profile, bool Inherited) ResolveProfile(CapabilityRoutingInput input)
    {
        var current = DetectIntent(input.UserInput ?? string.Empty).Intent;

        if (!string.Equals(current, "unknown", StringComparison.Ordinal))
        {
            return (ToProfile(current), false);
        }

        foreach (var previous in input.PreviousUserInputs ?? [])
        {
            if (string.IsNullOrWhiteSpace(previous))
            {
                continue;
            }

            var intent = DetectIntent(previous).Intent;

            if (!string.Equals(intent, "unknown", StringComparison.Ordinal))
            {
                return (ToProfile(intent), true);
            }
        }

        return (ToolTaskProfile.General, false);
    }

    /// <summary>任务档对应的固定工具集；<see cref="ToolTaskProfile.General"/> 不额外补包。</summary>
    public static IReadOnlyList<string> ToolsOfProfile(ToolTaskProfile profile) =>
        profile == ToolTaskProfile.General ? [] : ToolsOf(IntentOf(profile));

    private static ToolTaskProfile ToProfile(string intent) => intent switch
    {
        "read" => ToolTaskProfile.Read,
        "edit" => ToolTaskProfile.Edit,
        "run" => ToolTaskProfile.Run,
        "shell" => ToolTaskProfile.Shell,
        "pdf" => ToolTaskProfile.Document,
        "web" => ToolTaskProfile.Web,
        "memory" => ToolTaskProfile.Memory,
        "plan" => ToolTaskProfile.Plan,
        _ => ToolTaskProfile.General
    };

    private static string IntentOf(ToolTaskProfile profile) => profile switch
    {
        ToolTaskProfile.Read => "read",
        ToolTaskProfile.Edit => "edit",
        ToolTaskProfile.Run => "run",
        ToolTaskProfile.Shell => "shell",
        ToolTaskProfile.Document => "pdf",
        ToolTaskProfile.Web => "web",
        ToolTaskProfile.Memory => "memory",
        ToolTaskProfile.Plan => "plan",
        _ => "unknown"
    };

    /// <summary>规则意图的短标签（diagnostics / 测试用）。</summary>
    public static string ClassifyIntent(string input) => DetectIntent(input).Intent;

    /// <summary>
    /// 宿主自动追加的提示消息前缀（无修改回灌 / 自动验证）。它出现在历史里但是**不是用户输入**，
    /// 判任务档时必须跳过——「本轮没有检测到任何文件修改」不该被当成用户要改文件。
    /// </summary>
    public const string HostPromptPrefix = "[宿主提示]";

    /// <summary>这条用户消息是不是宿主自己追加的提示。</summary>
    public static bool IsHostPrompt(string? text) =>
        text is not null && text.TrimStart().StartsWith(HostPromptPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 从按时间顺序排列的用户输入里取出「本轮之前」的最近几条（新的在前，最多 <paramref name="limit"/> 条）。
    /// 调用方把历史直接交给 <see cref="CapabilityRoutingInput.PreviousUserInputs"/> 即可。
    /// </summary>
    public static IReadOnlyList<string> PreviousUserInputs(
        IReadOnlyList<string>? userInputsInOrder,
        int limit = 4)
    {
        if (userInputsInOrder is null || userInputsInOrder.Count <= 1 || limit <= 0)
        {
            return [];
        }

        var previous = new List<string>(Math.Min(limit, userInputsInOrder.Count - 1));

        for (var i = userInputsInOrder.Count - 2; i >= 0 && previous.Count < limit; i--)
        {
            // 宿主提示不是用户说的话，不参与任务档判定。
            if (!string.IsNullOrWhiteSpace(userInputsInOrder[i]) && !IsHostPrompt(userInputsInOrder[i]))
            {
                previous.Add(userInputsInOrder[i]);
            }
        }

        return previous;
    }

    /// <summary>
    /// 输入是否像“要改代码 / 跑命令”的任务，而不是寒暄或纯问答。
    /// 供“无修改回灌”判断用，避免把普通聊天强行拽进工具循环。
    /// </summary>
    public static bool LooksLikeTask(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        return text.Length >= 4 && HasTaskSignal(text);
    }

    /// <summary>
    /// M4：规则是否「不确定」到值得问一次小模型。只有同时满足以下条件才调用：
    /// Full、未全量暴露、工具数超过上限、输入有任务信号，且规则意图未知 / 多意图 / 命中的工具都不可用。
    /// </summary>
    public static bool ShouldConsultAdvisor(CapabilityRoutingInput input, RoutingDecision rule)
    {
        if (input.Mode != AgentMode.Full || input.ExposeAll || !rule.Truncated
            || rule.Intent == TurnIntent.Chat)
        {
            return false;
        }

        var text = (input.UserInput ?? string.Empty).Trim();
        if (text.Length < 4 || !HasTaskSignal(text))
        {
            return false;
        }

        var intents = DetectIntents(text);
        if (intents.Count == 0)
        {
            return true;
        }

        if (intents.Count > 1)
        {
            return true;
        }

        var available = Normalize(input.AvailableTools);
        var tools = IntentRules.First(rule => rule.Intent == intents[0]).Tools;
        return !tools.Any(tool => Matches(tool, available));
    }

    /// <summary>未知意图的 Compact 兜底：最小的「能看能搜能跑」集合。</summary>
    private static readonly string[] CompactFallback =
        ["list_tree", "read_file", "search_files", "run_command"];

    private static RoutingDecision Finalize(
        CapabilityRoutingInput input,
        IReadOnlyList<string> available,
        IReadOnlyList<string> orderedCandidates,
        string intent,
        string source,
        RoutingAdvice? advice,
        TurnIntent intentKind,
        ToolTaskProfile profile,
        bool profileInherited)
    {
        var limit = ResolveLimit(input, available.Count);

        // 全量暴露是排障逃生口：连 load_skill 也一起给出，不受「元工具单独计数」影响。
        if (input.ExposeAll)
        {
            var all = available.ToArray();
            return new RoutingDecision(
                all,
                CoreTools.Where(name => all.Contains(name, StringComparer.Ordinal)).ToArray(),
                all.Where(name => !CoreTools.Contains(name, StringComparer.Ordinal)
                                  && !string.Equals(name, EscapeHatchTool, StringComparison.Ordinal)).ToArray(),
                MaxExposedTools: all.Length,
                EstimatedSchemaTokens: ToolSchemaEstimator.Estimate(all),
                EscapeHatch: false,
                Truncated: false,
                Reason: $"{input.Mode} · 全量暴露 · {all.Length} 个工具")
            {
                Source = source,
                // 全量暴露是为了排障：对话轮也按工具轮处理，避免提示词说「不要用工具」却给了工具。
                Intent = intentKind == TurnIntent.Chat ? TurnIntent.Task : intentKind,
                Profile = profile,
                ProfileInherited = profileInherited
            };
        }

        // M2 的两个元工具（兜底入口 / 技能正文加载）在 Full 暴露；Compact 也暴露 load_skill，
        // 因为技能索引同样会进入 Compact 的系统提示，模型必须能取正文。
        var hasLoadSkill = !input.ExposeAll
                           && Matches(LoadSkillTool, available);
        var escapeHatch = input.Mode != AgentMode.Compact
                          && input.EscapeHatch
                          && !input.ExposeAll
                          && available.Count > 0;
        var metaCount = (escapeHatch ? 1 : 0) + (hasLoadSkill ? 1 : 0);
        var realLimit = Math.Max(0, limit - metaCount);
        var realAvailable = available
            .Where(name => !string.Equals(name, LoadSkillTool, StringComparison.Ordinal))
            .ToArray();

        var ordered = new List<string>();
        foreach (var name in orderedCandidates)
        {
            if (Matches(name, realAvailable) && !ordered.Contains(name, StringComparer.Ordinal))
            {
                ordered.Add(name);
            }
        }

        var exposedReal = ordered.Take(realLimit).ToList();
        var meta = new List<string>();

        if (escapeHatch)
        {
            meta.Add(EscapeHatchTool);
        }

        if (hasLoadSkill)
        {
            meta.Add(LoadSkillTool);
        }

        var exposed = exposedReal.Concat(meta).ToArray();

        var core = CoreTools
            .Where(name => exposed.Contains(name, StringComparer.Ordinal))
            .ToArray();

        var packs = exposed
            .Where(name => !CoreTools.Contains(name, StringComparer.Ordinal)
                           && !string.Equals(name, EscapeHatchTool, StringComparison.Ordinal))
            .ToArray();

        var truncated = realAvailable.Length > exposedReal.Count;
        var reason =
            $"{input.Mode} · 意图={intent} · 暴露 {exposed.Length}/{available.Count}"
            + (truncated ? $"（上限 {limit}）" : string.Empty);

        if (profile != ToolTaskProfile.General)
        {
            // 任务档是「这一轮按什么任务在给工具」；从历史轮沿用时要写出来，否则诊断看起来像没命中意图。
            reason += $" · 任务档={profile}" + (profileInherited ? "（沿用上一轮）" : string.Empty);
        }

        if (source == RoutingDecision.SmallModelSource)
        {
            reason += " · 小模型路由";
            if (advice is { Confidence: < 0.5 })
            {
                reason += "（低置信）";
            }
        }

        return new RoutingDecision(
            exposed,
            core,
            packs,
            MaxExposedTools: input.ExposeAll ? available.Count : limit,
            EstimatedSchemaTokens: ToolSchemaEstimator.EstimateWithEscapeHatch(exposed, escapeHatch),
            EscapeHatch: escapeHatch,
            Truncated: truncated,
            Reason: reason)
        {
            Source = source,
            Intent = intentKind,
            Profile = profile,
            ProfileInherited = profileInherited
        };
    }

    private static int ResolveLimit(CapabilityRoutingInput input, int availableCount)
    {
        var limit = input.MaxExposedTools > 0 ? input.MaxExposedTools : 1;

        if (input.Mode == AgentMode.Compact)
        {
            limit = Math.Min(limit, CompactMaxTools);
        }

        if (input.ExposeAll)
        {
            limit = availableCount;
        }

        return Math.Max(0, limit);
    }

    private static (string Intent, IReadOnlyList<string> Tools) DetectIntent(string input)
    {
        var text = (input ?? string.Empty).ToLowerInvariant();
        var matches = DetectIntents(text);
        return matches.Count == 0 ? ("unknown", []) : (matches[0], ToolsOf(matches[0]));
    }

    private static IReadOnlyList<string> DetectIntents(string text)
    {
        text = (text ?? string.Empty).ToLowerInvariant();
        return IntentRules
            .Where(rule => ContainsAny(text, rule.Words))
            .Select(rule => rule.Intent)
            .ToArray();
    }

    private static string[] ToolsOf(string intent) =>
        IntentRules.First(rule => rule.Intent == intent).Tools;

    private static bool HasTaskSignal(string text) =>
        ContainsAny(text.ToLowerInvariant(), TaskSignalWords);

    private static string[] Normalize(IReadOnlyList<string> available) =>
        available
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool ContainsAny(string text, IReadOnlyList<string> words) =>
        words.Any(word => text.Contains(word, StringComparison.Ordinal));

    private static bool Matches(string name, IReadOnlyList<string> available) =>
        available.Contains(name, StringComparer.Ordinal);

    private static void AddCandidates(
        List<string> target,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> available)
    {
        foreach (var candidate in candidates)
        {
            if (Matches(candidate, available) && !target.Contains(candidate, StringComparer.Ordinal))
            {
                target.Add(candidate);
            }
        }
    }
}
