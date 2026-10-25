using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P7：Full 模式也必须带编码工作流约束，不能只靠通用助手提示词。</summary>
public sealed class FullPromptTests
{
    [Fact]
    public void Full提示词包含定位与最小改动约束()
    {
        Assert.Contains("search_files", SystemPrompts.FullWithPlanLine);
        Assert.Contains("read_file", SystemPrompts.FullWithPlanLine);
        Assert.Contains("replace_text", SystemPrompts.FullWithPlanLine);
        Assert.Contains("不要把 write_file 当作补丁失败的默认兜底", SystemPrompts.FullWithPlanLine);
        Assert.Contains("不要谎报成功", SystemPrompts.FullWithPlanLine);
    }

    [Fact]
    public void Full提示词先判断任务类型再决定用不用工具()
    {
        Assert.Contains("先判断这一轮要做什么", SystemPrompts.FullWithPlanLine);
        Assert.Contains("不要调用工具", SystemPrompts.FullWithPlanLine);
        Assert.Contains("才调用工具", SystemPrompts.FullWithPlanLine);
    }

    [Fact]
    public void 有思考通道时不要求正文PLAN()
    {
        // P1b §3.2.5：PLAN 行是没有推理通道时的替代品；有思考通道就把计划留在思考里。
        var reasoning = SystemPrompts.FullFor(supportsReasoning: true);

        Assert.Equal(SystemPrompts.FullWithReasoning, reasoning);
        Assert.DoesNotContain("PLAN", reasoning);
        Assert.Contains("先在思考里定好", reasoning);

        // 规则本身一条都不能少：两种提示词共用同一份 TaskRules。
        Assert.Contains("不要把 write_file 当作补丁失败的默认兜底", reasoning);
        Assert.Contains("不要谎报成功", reasoning);
    }

    [Fact]
    public void 没有思考通道时保留PLAN约束()
    {
        var plain = SystemPrompts.FullFor(supportsReasoning: false);

        Assert.Equal(SystemPrompts.FullWithPlanLine, plain);
        Assert.Contains("PLAN", plain);
    }

    [Fact]
    public void 对话提示词明确不调用工具()
    {
        Assert.Contains("这一轮不需要工具", SystemPrompts.Chat);
        Assert.Contains("不要调用任何工具", SystemPrompts.Chat);
        Assert.DoesNotContain("PLAN", SystemPrompts.Chat);
    }
}
