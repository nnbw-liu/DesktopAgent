using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>工具结果状态：失败 / 被拒绝必须带标记，并计入本轮指标。</summary>
public sealed class ToolStatusTests
{
    [Fact]
    public async Task 编辑失败返回rejected标记并计数()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = false;
        var metrics = new AgentRunMetrics();
        var registry = new AgentToolRegistry([new FakeEditPack("未写入任何文件：第 1 个 hunk 未匹配")]);
        var context = new ToolContext(
            "session",
            temp.Root,
            [],
            settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            new FileReadRegistry());

        var tool = Assert.IsAssignableFrom<AIFunction>(
            registry.Create(context, new HashSet<string> { "edits" }, metrics)
                .Single(function => function.Name == "apply_patch"));

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = "a.cs"
        }));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("[TOOL_STATUS:rejected]", text);
        Assert.Equal(1, metrics.RejectedToolCalls);
        Assert.Equal(0, metrics.FailedToolCalls);
    }

    [Fact]
    public async Task 参数异常返回error标记并计数()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        var metrics = new AgentRunMetrics();
        var registry = new AgentToolRegistry([new FakeEditPack("不会执行")]);
        var context = new ToolContext(
            "session",
            temp.Root,
            [],
            settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            new FileReadRegistry());

        var tool = Assert.IsAssignableFrom<AIFunction>(
            registry.Create(context, new HashSet<string> { "edits" }, metrics)
                .Single(function => function.Name == "apply_patch"));

        // 需要 path，但故意不传，走 ArgumentException 分支。
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("[TOOL_STATUS:error]", text);
        Assert.Equal(1, metrics.FailedToolCalls);
    }

    private sealed class FakeEditPack(string result) : IAgentToolPack
    {
        public AgentToolPackDescriptor Descriptor { get; } = new(
            Id: "edits",
            Name: "测试编辑",
            Description: "测试用",
            Glyph: "✎",
            Risk: AgentToolRisk.Medium,
            Order: 21,
            DefaultEnabled: true,
            WindowsOnly: false,
            Functions:
            [
                new AgentToolFunctionDescriptor("apply_patch", "应用补丁", AgentToolRisk.Medium, RequiresApproval: true)
            ],
            BuiltIn: true);

        public IReadOnlyList<AITool> Create(ToolContext context) =>
        [
            AIFunctionFactory.Create((string path) => result, name: "apply_patch")
        ];
    }
}
