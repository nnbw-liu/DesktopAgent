namespace SDemo.DesktopAgent.Services;

/// <summary>工具 schema 预算的粗估：路由发生在工具实例化之前，只能按名字量级估算。</summary>
public static class ToolSchemaEstimator
{
    /// <summary>每个函数的固定开销（名称、描述、参数结构、分隔符）。实测后由宿主回填精确值。</summary>
    public const int PerToolCost = 130;

    /// <summary>兜底入口 <c>search_capabilities</c> 自身的粗略成本。</summary>
    public const int EscapeHatchCost = 150;

    /// <summary>按工具名估算一轮工具段的 token 成本。</summary>
    public static int Estimate(IReadOnlyList<string> toolNames)
    {
        var total = 0;

        foreach (var name in toolNames)
        {
            total += PerToolCost + Math.Max(0, name.Length);
        }

        return total;
    }

    /// <summary>追加兜底入口后的估算；兜底入口即使出现在工具名列表里也只按固定成本算一次。</summary>
    public static int EstimateWithEscapeHatch(IReadOnlyList<string> toolNames, bool escapeHatch)
    {
        var real = escapeHatch
            ? toolNames
                .Where(name => !string.Equals(name, "search_capabilities", StringComparison.Ordinal))
                .ToArray()
            : toolNames;

        return Estimate(real) + (escapeHatch ? EscapeHatchCost : 0);
    }
}
