using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P1b：曝光面按任务档稳定（§3.2.3）+ 工具描述里的决策规则（§3.2.2）。
/// </summary>
public sealed class ToolExposureProfileTests
{
    private static readonly string[] Available =
    [
        "read_file", "search_files", "apply_patch", "run_command", "todo_write", "load_skill",
        "read_skill_file",
        "replace_text", "write_file", "run_powershell", "pdf_read", "pdf_info", "web_search",
        "search_workspace_memory", "read_archived_session", "list_tree", "glob_files",
        "workspace_info", "search_capabilities"
    ];

    [Fact]
    public void 追问沿用上一轮的任务档_编辑工具不会被收走()
    {
        // 「继续」没有任务信号：以前会退化成核心集（丢掉 replace_text / write_file），
        // 现在沿用上一轮的档，工具面保持稳定。
        var decision = Route("继续", previous: ["帮我修一下这个 bug"]);

        Assert.Equal(ToolTaskProfile.Edit, decision.Profile);
        Assert.True(decision.ProfileInherited);
        Assert.Contains("replace_text", decision.ExposedTools);
        Assert.Contains("apply_patch", decision.ExposedTools);
        Assert.Contains("沿用上一轮", decision.Reason);
    }

    [Fact]
    public void 没有历史也没有任务信号时只给核心集()
    {
        var decision = Route("继续");

        Assert.Equal(ToolTaskProfile.General, decision.Profile);
        Assert.False(decision.ProfileInherited);
        Assert.DoesNotContain("replace_text", decision.ExposedTools);
        Assert.Contains("read_file", decision.ExposedTools);
    }

    [Fact]
    public void 换话题时任务档跟着变()
    {
        var decision = Route("跑一下构建", previous: ["帮我改一下这个 bug"]);

        Assert.Equal(ToolTaskProfile.Run, decision.Profile);
        Assert.False(decision.ProfileInherited);
        Assert.Contains("run_command", decision.ExposedTools);
    }

    [Fact]
    public void 本句有任务信号时优先于历史()
    {
        // 历史是读取，本句是编辑：本句赢（用户换了任务），而不是被历史拖住。
        var decision = Route("把端口改成 9090", previous: ["这个函数定义在哪个文件"]);

        Assert.Equal(ToolTaskProfile.Edit, decision.Profile);
        Assert.False(decision.ProfileInherited);
    }

    [Fact]
    public void 历史里最近的带任务信号的那一轮决定档()
    {
        var decision = Route("继续", previous: ["跑一下构建", "帮我修一下这个 bug"]);

        // PreviousUserInputs 是「新的在前」：最近一轮是跑构建 → Run。
        Assert.Equal(ToolTaskProfile.Run, decision.Profile);
    }

    [Fact]
    public void 历史输入取本轮之前的几条_新的在前()
    {
        var previous = CapabilityRouter.PreviousUserInputs(["一", "二", "三", "四", "五", "六"], limit: 3);

        Assert.Equal(["五", "四", "三"], previous);

        // 空历史 / 只有本轮 → 空。
        Assert.Empty(CapabilityRouter.PreviousUserInputs([]));
        Assert.Empty(CapabilityRouter.PreviousUserInputs(["只有本轮"]));

        // 空白输入跳过；limit 生效。
        Assert.Equal(["二", "一"], CapabilityRouter.PreviousUserInputs(["一", "  ", "二", "本轮"]));
        Assert.Equal(["二"], CapabilityRouter.PreviousUserInputs(["一", "  ", "二", "本轮"], limit: 1));

        // 宿主提示不是用户输入：跳过它，别让「本轮没有检测到任何文件修改」被当成编辑意图。
        Assert.Equal(
            ["帮我修一下这个 bug"],
            CapabilityRouter.PreviousUserInputs(
                ["帮我修一下这个 bug", "[宿主提示] 本轮没有检测到任何文件修改。", "继续"]));
    }

    [Fact]
    public void 没有历史时行为与M4之前一致()
    {
        // 单轮评测（eval/routing/cases.json）走的就是这条路径：历史为空 → 档 = 本句意图。
        var read = Route("这个函数定义在哪个文件");
        var edit = Route("帮我修一下这个 bug");

        Assert.Equal(ToolTaskProfile.Read, read.Profile);
        Assert.Equal(ToolTaskProfile.Edit, edit.Profile);
        Assert.Equal("edit", CapabilityRouter.ClassifyIntent("帮我修一下这个 bug"));
    }

    [Fact]
    public void 工具描述由功能说明与决策规则组成()
    {
        var composed = ToolPlaybooks.Compose("run_command", "运行白名单内的可执行文件。");

        Assert.StartsWith("运行白名单内的可执行文件。", composed, StringComparison.Ordinal);
        Assert.Contains("构建、测试", composed, StringComparison.Ordinal);
        Assert.Contains("run_powershell", composed, StringComparison.Ordinal);
        Assert.Contains("不要原样重试", composed, StringComparison.Ordinal);

        // 幂等：重复包装（Instrument 套 Instrument）不会把规则追加两遍。
        Assert.Equal(composed, ToolPlaybooks.Compose("run_command", composed));

        // 没有规则的工具保持原样。
        Assert.Equal("自定义工具。", ToolPlaybooks.Compose("my_custom_tool", "自定义工具。"));
    }

    [Fact]
    public void 关键工具都有决策规则_且规则成对互相指路()
    {
        // 「选谁」的先验必须写在描述里，而不是只留在宿主代码里。
        foreach (var tool in new[]
                 {
                     "read_file", "search_files", "glob_files", "apply_patch", "replace_text",
                     "write_file", "run_command", "run_powershell", "pdf_read", "web_search",
                     "todo_write", "load_skill", "read_skill_file", "search_capabilities"
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(ToolPlaybooks.RulesFor(tool)), tool);
        }

        Assert.Contains("search_files", ToolPlaybooks.RulesFor("read_file")!, StringComparison.Ordinal);
        Assert.Contains("read_file", ToolPlaybooks.RulesFor("run_command")!, StringComparison.Ordinal);
        Assert.Contains("run_command", ToolPlaybooks.RulesFor("run_powershell")!, StringComparison.Ordinal);
        Assert.Contains("默认兜底", ToolPlaybooks.RulesFor("write_file")!, StringComparison.Ordinal);
        Assert.Contains("本轮内补", ToolPlaybooks.RulesFor("search_capabilities")!, StringComparison.Ordinal);
    }

    private static RoutingDecision Route(string input, IReadOnlyList<string>? previous = null) =>
        CapabilityRouter.Route(new CapabilityRoutingInput(
            input,
            [],
            Available,
            AgentMode.Full,
            MaxExposedTools: 12,
            PreviousUserInputs: previous));
}
