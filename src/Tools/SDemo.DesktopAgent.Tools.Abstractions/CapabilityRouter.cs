using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>一轮路由的输入（§4.2 / §4.3）：用户原句、最近轨迹、可用工具、模式与上限。</summary>
public sealed record CapabilityRoutingInput(
    string UserInput,
    IReadOnlyList<string> RecentToolIds,
    IReadOnlyList<string> AvailableTools,
    AgentMode Mode,
    int MaxExposedTools,
    bool EscapeHatch = true,
    bool ExposeAll = false);

/// <summary>
/// 规则版能力路由（M1，成本档 0）：关键词 + 核心集 + 最近工具 + 模式，只在可用工具集合内选择。
/// 一轮只路由一次；Compact 走原来的四件套逻辑，Full 也从「全量暴露」收紧到「核心集 + 按需包」。
/// </summary>
public static class CapabilityRouter
{
    /// <summary>Compact 模式的上限，沿用 P6 的实测结论。</summary>
    public const int CompactMaxTools = 4;

    /// <summary>路由失败时的兜底入口：列出当前会话可用的工具。</summary>
    public const string EscapeHatchTool = "search_capabilities";

    /// <summary>技能正文加载（M2）：系统提示只放索引，正文按需读取。</summary>
    public const string LoadSkillTool = "load_skill";

    /// <summary>
    /// 核心集（§4.4）：只读、定位、修改、执行、计划各一个代表，外加两个元工具。
    /// 只有在可用集合里出现时才会暴露，用户关掉某个包不会凭空多出工具。
    /// </summary>
    public static readonly IReadOnlyList<string> CoreTools =
        ["read_file", "search_files", "apply_patch", "run_command", "todo_write", LoadSkillTool];

    private static readonly string[] EditWords =
        ["改", "修", "加", "重构", "实现", "新增", "删除", "测试", "fix", "add", "implement", "refactor"];

    private static readonly string[] ReadWords =
        ["查", "解释", "在哪", "为什么", "怎么", "what", "where", "why", "explain", "find"];

    private static readonly string[] RunWords =
        ["跑", "构建", "测试", "编译", "build", "test", "run", "compile"];

    private static readonly string[] PdfWords = ["pdf", "扫描件", "文档页"];

    private static readonly string[] ShellWords = ["powershell", "脚本", "沙箱"];

    private static readonly string[] WebWords = ["联网", "网上", "网页", "web", "搜索一下"];

    private static readonly string[] TodoWords = ["计划", "待办", "步骤", "todo"];

    /// <summary>按规则选出一轮暴露的工具；结果永远在 <paramref name="input"/> 的可用集合内。</summary>
    public static RoutingDecision Route(CapabilityRoutingInput input)
    {
        var available = input.AvailableTools
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (available.Length == 0)
        {
            return RoutingDecision.Empty with
            {
                MaxExposedTools = Math.Max(0, input.MaxExposedTools),
                Reason = "没有可用工具"
            };
        }

        var limit = input.MaxExposedTools > 0 ? input.MaxExposedTools : 1;

        if (input.Mode == AgentMode.Compact)
        {
            limit = Math.Min(limit, CompactMaxTools);
        }

        if (input.ExposeAll)
        {
            limit = available.Length;
        }

        var (intent, intentTools) = DetectIntent(input.UserInput);
        var ordered = new List<string>();

        // M2 的两个元工具（兜底入口 / 技能正文加载）在 Full 暴露；Compact 也暴露 load_skill，
        // 因为技能索引同样会进入 Compact 的系统提示，模型必须能取正文。
        var hasLoadSkill = !input.ExposeAll
                           && available.Contains(LoadSkillTool, StringComparer.Ordinal);
        var escapeHatch = input.Mode != AgentMode.Compact
                          && input.EscapeHatch
                          && !input.ExposeAll
                          && available.Length > 0;
        var metaCount = (escapeHatch ? 1 : 0) + (hasLoadSkill ? 1 : 0);
        var realLimit = Math.Max(0, limit - metaCount);
        var realAvailable = available
            .Where(name => !string.Equals(name, LoadSkillTool, StringComparison.Ordinal))
            .ToArray();

        if (input.Mode == AgentMode.Compact)
        {
            // Compact 保持 P6 行为：意图优先，最近用过的工具插到最前面，末位兜底四件套。
            AddCandidates(ordered, intentTools, available);

            foreach (var recent in input.RecentToolIds.Reverse())
            {
                if (Matches(recent, realAvailable) && !ordered.Contains(recent, StringComparer.Ordinal))
                {
                    ordered.Insert(0, recent);
                }
            }

            AddCandidates(ordered, CompactFallback, available);
        }
        else
        {
            // Full：核心集永远先来，再按意图补包，最后按工具包 Order 填满剩余名额。
            AddCandidates(ordered, CoreTools, realAvailable);
            AddCandidates(ordered, intentTools, realAvailable);

            foreach (var recent in input.RecentToolIds.Reverse())
            {
                if (Matches(recent, realAvailable) && !ordered.Contains(recent, StringComparer.Ordinal))
                {
                    ordered.Add(recent);
                }
            }

            AddCandidates(ordered, realAvailable, realAvailable);
        }

        // 最近轨迹里可能有已经不可用的工具名：统一按可用集合过滤一次。
        var exposedReal = ordered
            .Where(name => Matches(name, realAvailable))
            .Take(realLimit)
            .ToList();

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

        var reason =
            $"{input.Mode} · 意图={intent} · 暴露 {exposed.Length}/{available.Length}"
            + (realAvailable.Length > exposedReal.Count ? $"（上限 {limit}）" : string.Empty);

        return new RoutingDecision(
            exposed,
            core,
            packs,
            MaxExposedTools: input.ExposeAll ? available.Length : limit,
            EstimatedSchemaTokens: ToolSchemaEstimator.EstimateWithEscapeHatch(exposed, escapeHatch),
            EscapeHatch: escapeHatch,
            Truncated: realAvailable.Length > exposedReal.Count,
            Reason: reason);
    }

    /// <summary>未知意图的 Compact 兜底：最小的「能看能搜能跑」集合。</summary>
    private static readonly string[] CompactFallback =
        ["list_tree", "read_file", "search_files", "run_command"];

    private static (string Intent, IReadOnlyList<string> Tools) DetectIntent(string input)
    {
        var text = input.ToLowerInvariant();

        if (ContainsAny(text, EditWords))
        {
            // 小模型优先用「精确替换」；apply_patch 失败后还有 write_file 兜底。
            return ("edit", ["read_file", "replace_text", "apply_patch", "write_file"]);
        }

        if (ContainsAny(text, PdfWords))
        {
            return ("pdf", ["pdf_read", "pdf_info", "read_file"]);
        }

        if (ContainsAny(text, ShellWords))
        {
            return ("shell", ["run_powershell", "read_file"]);
        }

        if (ContainsAny(text, RunWords))
        {
            return ("run", ["run_command", "read_file"]);
        }

        if (ContainsAny(text, WebWords))
        {
            return ("web", ["web_search"]);
        }

        if (ContainsAny(text, TodoWords))
        {
            return ("plan", ["todo_write"]);
        }

        if (ContainsAny(text, ReadWords))
        {
            return ("read", ["search_files", "read_file", "list_tree"]);
        }

        return ("unknown", []);
    }

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
