using System.Text;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 当前策略段（P2a 新增，**required**）。
/// <para>
/// 提示词里没有任何「现在能做什么」的描述：用户切到直跑模式、把工作区副本改成只读、
/// 或者改审批策略之后，模型完全不知道 —— 而它仍然会声称「已经写入真实工作区」。
/// 这一段把模式、沙箱、直跑、写回确认四件事写成可机读的短句，每轮都在。
/// </para>
/// </summary>
public sealed class PolicyContextSection : IAgentContextSection
{
    public string Name => "policy";

    public string Title => "当前策略";

    public int Order => ContextSectionOrders.Policy;

    public bool Required => true;

    public int MaxTokens => 200;

    public string? Render(ContextSectionInput input) =>
        Build(input.Mode, input.Profile, input.Policy);

    /// <summary>渲染策略正文；拆出来是为了让 golden test 直接断言「改设置 → 文本变化」。</summary>
    public static string Build(AgentMode mode, ModelProfile profile, AgentPolicyFacts policy)
    {
        var builder = new StringBuilder();

        builder.Append("- 本轮模式：")
            .AppendLine(mode == AgentMode.Compact
                ? $"紧凑（小窗口，窗口 {FormatWindow(profile.ContextWindow)}）"
                : $"完整（窗口 {FormatWindow(profile.ContextWindow)}）");

        builder.Append("- 沙箱：").AppendLine(DescribeSandbox(policy));

        builder.Append("- 直跑：")
            .AppendLine(policy.DirectRunAllowed
                ? "允许白名单命令在宿主直跑（仍逐条校验参数）"
                : "禁止（命令一律在沙箱内执行）");

        if (policy.CommandAllowlist.Count > 0)
        {
            builder.Append("- 命令白名单：")
                .AppendLine(string.Join(", ", policy.CommandAllowlist.Take(12)));
        }

        builder.Append("- 写入真实工作区：")
            .AppendLine(policy.ConfirmFileWrite ? "需要用户在界面确认后才生效" : "直接生效（已关闭逐次确认）");

        return builder.ToString().TrimEnd();
    }

    private static string DescribeSandbox(AgentPolicyFacts policy)
    {
        if (!policy.SandboxEnabled)
        {
            return "关闭（高风险命令与 PDF 解析不可用，不要声称执行过）";
        }

        var builder = new StringBuilder("启用 · ")
            .Append(Kind(policy.SandboxKind))
            .Append(policy.SandboxRequirement == SandboxRequirement.Required ? " · 必需" : " · 可降级")
            .Append(policy.WorkspaceReadOnly ? " · 工作区副本只读" : " · 工作区副本可写");

        return builder.ToString();
    }

    private static string Kind(SandboxKind kind) => kind switch
    {
        SandboxKind.AppContainer => "AppContainer",
        SandboxKind.WindowsSandbox => "Windows Sandbox",
        SandboxKind.JobObject => "JobObject",
        SandboxKind.Remote => "Remote",
        _ => "None"
    };

    private static string FormatWindow(int window) =>
        window >= 1024 ? $"{window / 1024}K" : window.ToString();
}
