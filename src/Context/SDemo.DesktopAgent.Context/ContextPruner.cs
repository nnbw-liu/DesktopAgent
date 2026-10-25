namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 压缩前的确定性修剪（P2b §3.3.2 第一条：**先修剪，后摘要**）。
/// <para>
/// 修剪不调模型、不改会话原文，只决定「这次请求回放多少工具轨迹」。它排在「淘汰整轮历史」之前：
/// 少给几行工具摘要（模型随时可以用工具重跑 / 重读），总比让模型忘掉一整轮用户与助手的对话便宜。
/// 修剪之后仍然放不下，才轮到摘要（要花模型调用）和整轮淘汰。
/// </para>
/// <para>
/// 对应 DSH 的 <c>dsh-compaction-tool-result-pruner</c>：那边是「超 8192 字符的工具结果改成头尾预览」，
/// 我们这边的等价物是「工具轨迹按 token 预算回放」（<see cref="ContextPolicy.ToolTraceTokenBudget"/>）。
/// 差别在于 DSH 的 pruner 只在压缩被触发后运行，我们把它提前成**第一档手段**——
/// 修完够用就整次跳过摘要，一次模型调用都不发生。
/// </para>
/// </summary>
public static class ContextPruner
{
    /// <summary>降到下一档时的最小步长；预算太小直接归零，避免 1 → 0 之间的空转。</summary>
    private const int MinStep = 100;

    /// <summary>
    /// 修剪档位：配置值 → 逐次减半 → 0。第一档就是「什么都不修」，所以序列至少有一项。
    /// 顺序即优先级：越靠前越不伤信息量。
    /// </summary>
    public static IReadOnlyList<int> TraceTiers(int configuredBudget)
    {
        var budget = Math.Max(0, configuredBudget);
        var tiers = new List<int> { budget };

        while (budget > 0)
        {
            budget = budget >= MinStep ? budget / 2 : 0;
            tiers.Add(budget);
        }

        return tiers;
    }

    /// <summary>修剪是否可做：没有配置轨迹预算就没什么可修的。</summary>
    public static bool CanPrune(int configuredBudget) => configuredBudget > 0;
}
