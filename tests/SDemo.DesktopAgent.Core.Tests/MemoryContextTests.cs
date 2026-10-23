using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M3：知识库索引行进入固定块，计入 Knowledge 段；关闭时成本为 0。</summary>
public sealed class MemoryContextTests
{
    private readonly HeuristicTokenCounter _counter = new();
    private readonly StubSettingsService _settings = new();

    [Fact]
    public void 开启知识库只增加一小段索引且计入独立分段()
    {
        var builder = new ContextBuilder(_counter, _settings);
        var request = new ContextBuildRequest(
            Provider(),
            "m",
            [new ChatMessage(ChatMessageRole.User, "上次沙箱 ACL 的结论")],
            MemoryText: "\n\n## 工作区知识库\n本工作区有 20 条归档会话。需要回忆过去讨论时，用 search_workspace_memory 检索。\n");

        var plan = builder.Build(request);

        Assert.Contains("工作区知识库", plan.Instructions);
        Assert.Contains("20 条归档会话", plan.Instructions);
        Assert.True(plan.Report.Segments.Knowledge > 0);
        Assert.True(plan.Report.Segments.Knowledge <= plan.Budget.KnowledgeLimit);
        Assert.Equal(plan.Report.Segments.Total, plan.Report.Estimated);
    }

    [Fact]
    public void 没有知识库时不注入且分段为零()
    {
        var builder = new ContextBuilder(_counter, _settings);
        var plan = builder.Build(new ContextBuildRequest(
            Provider(),
            "m",
            [new ChatMessage(ChatMessageRole.User, "你好")]));

        Assert.DoesNotContain("工作区知识库", plan.Instructions);
        Assert.Equal(0, plan.Report.Segments.Knowledge);
        Assert.Equal(0, plan.Report.KnowledgeTokens);
    }

    [Fact]
    public void 索引行超长时按知识库预算截断()
    {
        var builder = new ContextBuilder(_counter, _settings);
        var text = "\n\n## 工作区知识库\n" + string.Concat(Enumerable.Repeat("本工作区有很多归档会话。", 200));

        var plan = builder.Build(new ContextBuildRequest(
            Provider(),
            "m",
            [new ChatMessage(ChatMessageRole.User, "你好")],
            MemoryText: text));

        Assert.True(plan.Report.Segments.Knowledge <= plan.Budget.KnowledgeLimit);
        Assert.Contains("知识库索引已截断", plan.Instructions);
    }

    private static ModelProvider Provider() => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        Models =
        [
            new ModelEntry
            {
                Id = "m",
                Profile = ModelProfilePresets.FromPreset("8K")
            }
        ]
    };
}
