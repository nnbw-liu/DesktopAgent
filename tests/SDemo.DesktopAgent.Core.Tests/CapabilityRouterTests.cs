using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// M1 规则路由：Compact 保持 P6 行为，Full 改为「核心集 + 按需包 + 上限」；
/// 关键词、最近轨迹、全量暴露与兜底入口都在这里钉住。
/// </summary>
public sealed class CapabilityRouterTests
{
    private static readonly string[] Available =
    [
        "read_file", "search_files", "apply_patch", "run_command", "list_tree", "write_file"
    ];

    [Fact]
    public void 修改意图优先编辑工具()
    {
        var selected = Select("帮我改一下这个文件", AgentMode.Compact);

        Assert.Contains("apply_patch", selected);
        Assert.Contains("read_file", selected);
        Assert.True(selected.Count <= 4);
    }

    [Fact]
    public void 查询意图不选编辑工具()
    {
        var selected = Select("这个函数在哪里", AgentMode.Compact);

        Assert.Contains("search_files", selected);
        Assert.DoesNotContain("apply_patch", selected);
    }

    [Fact]
    public void 运行意图优先run_command()
    {
        var selected = Select("跑一下构建", AgentMode.Compact);

        Assert.Equal("run_command", selected[0]);
    }

    [Fact]
    public void 纯对话不暴露任何工具()
    {
        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "嗯",
            [],
            Available,
            AgentMode.Compact,
            MaxExposedTools: 12));

        Assert.Empty(decision.ExposedTools);
        Assert.Equal(TurnIntent.Chat, decision.Intent);
        Assert.False(decision.NeedsTools);
        Assert.Contains("意图=chat", decision.Reason);
    }

    [Fact]
    public void 未知任务意图给保守四件套()
    {
        var selected = Select("处理一下那个东西", AgentMode.Compact);

        Assert.Equal(["list_tree", "read_file", "search_files", "run_command"], selected);
    }

    [Fact]
    public void 最近工具优先复用()
    {
        var selected = Select("改代码", AgentMode.Compact, recent: ["search_files"]);

        Assert.Equal("search_files", selected[0]);
    }

    [Fact]
    public void Full模式核心集永远先来且受上限约束()
    {
        var available = Enumerable.Range(0, 30).Select(index => $"tool_{index}").ToArray();
        var core = new[] { "read_file", "search_files", "apply_patch", "run_command", "todo_write" };
        var union = core.Concat(available).ToArray();

        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "帮我改一下这个文件",
            [],
            union,
            AgentMode.Full,
            MaxExposedTools: 6));

        Assert.Equal(6, decision.Count);
        Assert.True(decision.Truncated);
        Assert.All(core, name => Assert.Contains(name, decision.ExposedTools));
        Assert.All(decision.ExposedTools.Take(core.Length), name => Assert.Contains(name, core));
    }

    [Fact]
    public void Full模式按意图补包而不是全量()
    {
        var available = Available
            .Concat(["pdf_read", "pdf_info", "run_powershell", "web_search"])
            .ToArray();

        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "帮我读一下这个 PDF",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 6));

        Assert.Contains("pdf_read", decision.ExposedTools);
        Assert.DoesNotContain("web_search", decision.ExposedTools);
        Assert.True(decision.Count <= 6);
        Assert.Equal(decision.EstimatedSchemaTokens, ToolSchemaEstimator.EstimateWithEscapeHatch(
            decision.ExposedTools, decision.EscapeHatch));
    }

    [Fact]
    public void Full模式提供兜底入口()
    {
        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "看一下这个文件",
            [],
            Available,
            AgentMode.Full,
            MaxExposedTools: 12));

        Assert.True(decision.EscapeHatch);
        Assert.Contains(CapabilityRouter.EscapeHatchTool, decision.ExposedTools);
        Assert.Empty(decision.PackTools.Intersect(decision.CoreTools, StringComparer.Ordinal));
    }

    [Fact]
    public void 全量暴露时按可用集合全部给出()
    {
        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "嗯",
            [],
            Available,
            AgentMode.Full,
            MaxExposedTools: 6,
            ExposeAll: true));

        Assert.Equal(Available.Length, decision.Count);
        Assert.False(decision.Truncated);
        Assert.Equal(Available.Length, decision.MaxExposedTools);
        // 全量暴露是排障逃生口：即使输入是寒暄，也按工具轮处理，不能又给工具又说「不要用工具」。
        Assert.True(decision.NeedsTools);
    }

    [Fact]
    public void 没有可用工具时返回空决策()
    {
        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "帮我改代码",
            [],
            [],
            AgentMode.Full,
            MaxExposedTools: 6));

        Assert.Empty(decision.ExposedTools);
        Assert.Equal(0, decision.EstimatedSchemaTokens);
    }

    [Fact]
    public void 技能工具与兜底入口各占一个名额()
    {
        var available = Available.Concat(["load_skill"]).ToArray();

        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "帮我改一下这个文件",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 6));

        Assert.Contains(CapabilityRouter.LoadSkillTool, decision.ExposedTools);
        Assert.Contains(CapabilityRouter.EscapeHatchTool, decision.ExposedTools);
        Assert.Equal(6, decision.Count);
        Assert.True(decision.ExposedTools.Count(name =>
            !string.Equals(name, CapabilityRouter.LoadSkillTool, StringComparison.Ordinal)
            && !string.Equals(name, CapabilityRouter.EscapeHatchTool, StringComparison.Ordinal)) <= 4);
    }

    [Fact]
    public void Compact也暴露load_skill但总量仍不超过四()
    {
        var available = Available.Concat(["load_skill"]).ToArray();

        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "看一下这个文件",
            [],
            available,
            AgentMode.Compact,
            MaxExposedTools: 12));

        Assert.Equal(CapabilityRouter.CompactMaxTools, decision.Count);
        Assert.Contains(CapabilityRouter.LoadSkillTool, decision.ExposedTools);
        Assert.DoesNotContain(CapabilityRouter.EscapeHatchTool, decision.ExposedTools);
    }

    [Fact]
    public void 回忆意图补知识库工具且不会被沙箱关键词抢走()
    {
        var available = Available
            .Concat(["run_powershell", "pdf_read", CapabilityRouter.SearchMemoryTool, CapabilityRouter.ReadMemoryTool])
            .ToArray();

        var decision = CapabilityRouter.Route(new CapabilityRoutingInput(
            "上次关于沙箱 ACL 的结论",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 12));

        Assert.Contains(CapabilityRouter.SearchMemoryTool, decision.ExposedTools);
        Assert.Contains(CapabilityRouter.ReadMemoryTool, decision.ExposedTools);
        // 意图判定必须是 memory 而不是 shell：无关的 run_powershell 不应因为「填满名额」被带上。
        Assert.Contains("意图=memory", decision.Reason);
        Assert.DoesNotContain("run_powershell", decision.ExposedTools);
        Assert.Equal(TurnIntent.Answer, decision.Intent);
    }

    [Fact]
    public void 按窗口解析默认暴露上限()
    {
        var settings = new AgentSettings();

        Assert.Equal(AgentSettings.SmallWindowMaxExposedTools, settings.ResolveMaxExposedTools(8192));
        Assert.Equal(AgentSettings.LargeWindowMaxExposedTools, settings.ResolveMaxExposedTools(32 * 1024));

        settings.MaxExposedTools = 3;
        Assert.Equal(3, settings.ResolveMaxExposedTools(32 * 1024));

        settings.ExposeAllTools = true;
        Assert.Equal(int.MaxValue, settings.ResolveMaxExposedTools(8192));
    }

    [Fact]
    public void Compact模式仍受CompactMaxTools约束()
    {
        var settings = new AgentSettings { CompactMaxTools = 3 };

        Assert.Equal(3, settings.ResolveMaxExposedTools(8192, AgentMode.Compact));
        Assert.Equal(6, settings.ResolveMaxExposedTools(8192, AgentMode.Full));

        settings.ExposeAllTools = true;
        Assert.Equal(int.MaxValue, settings.ResolveMaxExposedTools(8192, AgentMode.Compact));
    }

    [Fact]
    public void 小模型建议优先但仍在可用集合与上限内()
    {
        var available = Available
            .Concat(["pdf_read", "pdf_info", "run_powershell", "web_search"])
            .ToArray();
        var input = new CapabilityRoutingInput(
            "帮我看看这个 PDF 并总结",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 6);
        var rule = CapabilityRouter.Route(input);

        var decision = CapabilityRouter.ApplyAdvice(
            input,
            new RoutingAdvice(["pdf_read", "not_a_tool", "read_file"], "pdf", "需要读 PDF", 0.9),
            rule);

        Assert.Equal(RoutingDecision.SmallModelSource, decision.Source);
        Assert.Contains("pdf_read", decision.ExposedTools);
        Assert.DoesNotContain("not_a_tool", decision.ExposedTools);
        Assert.True(decision.Count <= 6);
        Assert.Contains("意图=pdf", decision.Reason);
        Assert.Contains("小模型路由", decision.Reason);
    }

    [Fact]
    public void 小模型建议全部非法时沿用规则()
    {
        var input = new CapabilityRoutingInput(
            "帮我改一下这个文件",
            [],
            Available,
            AgentMode.Full,
            MaxExposedTools: 6);
        var rule = CapabilityRouter.Route(input);

        var decision = CapabilityRouter.ApplyAdvice(
            input,
            new RoutingAdvice(["made_up_tool"], "edit", null, 0.4),
            rule);

        Assert.Equal(RoutingDecision.RulesSource, decision.Source);
        Assert.Equal(rule.ExposedTools, decision.ExposedTools);
        Assert.Contains("沿用规则", decision.Reason);
    }

    [Fact]
    public void 规则不确定时才建议问小模型()
    {
        var available = Enumerable.Range(0, 12).Select(index => $"tool_{index}")
            .Concat(CapabilityRouter.CoreTools)
            .Concat(["pdf_read", "web_search", CapabilityRouter.SearchMemoryTool])
            .ToArray();

        var ambiguous = new CapabilityRoutingInput(
            "帮我处理一下那个东西",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 6);
        Assert.True(CapabilityRouter.ShouldConsultAdvisor(
            ambiguous, CapabilityRouter.Route(ambiguous)));

        var chatter = new CapabilityRoutingInput(
            "你好",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 6);
        Assert.False(CapabilityRouter.ShouldConsultAdvisor(
            chatter, CapabilityRouter.Route(chatter)));

        var clear = new CapabilityRoutingInput(
            "跑一下构建",
            [],
            available,
            AgentMode.Full,
            MaxExposedTools: 6);
        Assert.False(CapabilityRouter.ShouldConsultAdvisor(clear, CapabilityRouter.Route(clear)));

        var compact = ambiguous with { Mode = AgentMode.Compact };
        Assert.False(CapabilityRouter.ShouldConsultAdvisor(compact, CapabilityRouter.Route(compact)));
    }

    private static IReadOnlyList<string> Select(
        string input,
        AgentMode mode,
        string[]? recent = null) =>
        CapabilityRouter.Route(new CapabilityRoutingInput(
            input,
            recent ?? [],
            Available,
            mode,
            MaxExposedTools: 12)).ExposedTools;
}
