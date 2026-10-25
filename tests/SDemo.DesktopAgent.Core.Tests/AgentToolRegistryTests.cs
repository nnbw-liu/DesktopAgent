using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>工具包注册表：平台过滤、按本轮启用集合生成工具、调用计数。</summary>
public sealed class AgentToolRegistryTests
{
    [Fact]
    public void 描述按Order排序且能找到()
    {
        var registry = new AgentToolRegistry([Pack("b", order: 20), Pack("a", order: 10)]);

        Assert.Equal(["a", "b"], registry.Descriptors.Select(descriptor => descriptor.Id));
        Assert.NotNull(registry.Find("a"));
        Assert.Null(registry.Find("missing"));
    }

    [Fact]
    public void 未启用的包不会生成工具()
    {
        var registry = new AgentToolRegistry([Pack("files", order: 10)]);
        var context = Context();

        Assert.Empty(registry.Create(context, new HashSet<string>()));

        var tools = registry.Create(context, new HashSet<string> { "files" });
        Assert.Single(tools);
        Assert.Equal("fake_tool", tools[0].Name);
    }

    [Fact]
    public async Task 生成的工具会被计数()
    {
        var registry = new AgentToolRegistry([Pack("files", order: 10)]);
        var metrics = new AgentRunMetrics();
        var tool = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(registry.Create(Context(), new HashSet<string> { "files" }, metrics)));

        await tool.InvokeAsync(new AIFunctionArguments());

        Assert.Equal(1, metrics.ToolCalls["fake_tool"]);
    }

    [Fact]
    public void WindowsOnly按当前平台过滤()
    {
        var descriptor = Descriptor("win", windowsOnly: true);
        Assert.Equal(OperatingSystem.IsWindows(), AgentToolRegistry.IsPlatformSupported(descriptor));
    }

    [Fact]
    public void 可用工具名按包顺序展开()
    {
        var registry = new AgentToolRegistry([Pack("b", order: 20), Pack("a", order: 10)]);

        Assert.Equal(["fake_tool", "fake_tool2"], registry.ToolNames(new HashSet<string> { "a", "b" }));
        Assert.Empty(registry.ToolNames(new HashSet<string>()));
    }

    [Fact]
    public void 只生成路由选中的工具()
    {
        var registry = new AgentToolRegistry([Pack("a", order: 10)]);
        var context = Context();

        var tools = registry.CreateExposed(
            context,
            new HashSet<string> { "a" },
            new HashSet<string> { "fake_tool" });

        Assert.Single(tools);
        Assert.Equal("fake_tool", tools[0].Name);
    }

    [Fact]
    public async Task 兜底入口能列出可用工具()
    {
        var registry = new AgentToolRegistry([Pack("a", order: 10)]);
        var tools = registry.CreateExposed(
            Context(),
            new HashSet<string> { "a" },
            new HashSet<string> { "fake_tool" },
            escapeHatch: true);

        var function = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, tool => tool.Name == CapabilityRouter.EscapeHatchTool));
        var query = function.JsonSchema.GetProperty("properties").GetProperty("query");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, query.ValueKind);

        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "fake" }));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("fake_tool", text);
        Assert.Contains("已暴露", text);
    }

    [Fact]
    public async Task 兜底入口把命中但未暴露的工具报给宿主()
    {
        // P1b §3.2.4：宿主不再只是看着模型「下一轮再说」，而是拿到结构化命中结果在本轮补工具。
        var registry = new AgentToolRegistry([Pack("a", order: 10)]);
        CapabilitySearchOutcome? outcome = null;

        var tools = registry.CreateExposed(
            Context(),
            new HashSet<string> { "a" },
            new HashSet<string> { "fake_tool" },
            escapeHatch: true,
            onCapabilitySearch: captured => outcome = captured);

        var function = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, tool => tool.Name == CapabilityRouter.EscapeHatchTool));

        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "fake" }));

        Assert.NotNull(outcome);
        Assert.Equal("fake", outcome!.Query);
        Assert.Contains("fake_tool", outcome.Matched);
        // fake_tool 本轮已经暴露，只报没暴露的那个。
        Assert.Equal(["fake_tool2"], outcome.Unexposed);

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("本轮未暴露", text, StringComparison.Ordinal);
        Assert.Contains("本轮内补一次", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 模型看到的描述里带决策规则()
    {
        var registry = new AgentToolRegistry([new DescribedPack()]);
        var tool = Assert.Single(registry.Create(Context(), new HashSet<string> { "files" }));

        Assert.StartsWith("读工作区文件。", tool.Description, StringComparison.Ordinal);
        Assert.Contains(ToolPlaybooks.RulesFor("read_file")!, tool.Description, StringComparison.Ordinal);
    }

    private static IAgentToolPack Pack(string id, int order) =>
        new FakePack(new AgentToolPackDescriptor(
            id,
            id,
            "测试包",
            "T",
            AgentToolRisk.Low,
            order,
            DefaultEnabled: false,
            WindowsOnly: false,
            [
                new AgentToolFunctionDescriptor("fake_tool", "假工具"),
                new AgentToolFunctionDescriptor("fake_tool2", "假工具二")
            ]));

    private static AgentToolPackDescriptor Descriptor(string id, bool windowsOnly) => new(
        id,
        id,
        "测试包",
        "T",
        AgentToolRisk.Low,
        0,
        DefaultEnabled: false,
        windowsOnly,
        [new AgentToolFunctionDescriptor("fake_tool", "假工具")]);

    private static ToolContext Context()
    {
        var settings = new StubSettingsService();
        return new ToolContext(
            "session",
            Path.GetTempPath(),
            [],
            settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog());
    }

    private sealed class FakePack(AgentToolPackDescriptor descriptor) : IAgentToolPack
    {
        public AgentToolPackDescriptor Descriptor { get; } = descriptor;

        public IReadOnlyList<AITool> Create(ToolContext context) =>
            [AIFunctionFactory.Create(FakeTool, name: "fake_tool")];

        private static string FakeTool() => "ok";
    }

    /// <summary>带基础描述的工具包：验证「功能说明 + 决策规则」拼在模型看到的那条描述里。</summary>
    private sealed class DescribedPack : IAgentToolPack
    {
        public AgentToolPackDescriptor Descriptor { get; } = new(
            "files",
            "文件",
            "测试包",
            "T",
            AgentToolRisk.Low,
            10,
            DefaultEnabled: false,
            WindowsOnly: false,
            [new AgentToolFunctionDescriptor("read_file", "读文件")]);

        public IReadOnlyList<AITool> Create(ToolContext context) =>
            [AIFunctionFactory.Create(() => "ok", name: "read_file", description: "读工作区文件。")];
    }
}

/// <summary>测试共用的沙箱占位实现：文件工具与注册表测试不需要真的启动沙箱。</summary>
internal sealed class StubSandboxJobService : ISandboxJobService
{
#pragma warning disable CS0067
    public event EventHandler<SandboxJobOutcome>? OutcomeAvailable;
#pragma warning restore CS0067

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxAvailability(
            SandboxKind.None,
            Available: false,
            Verified: false,
            "测试环境没有沙箱。",
            Detail: null));

    public Task<SandboxAvailability> GetAvailabilityAsync(
        SandboxKind kind,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxAvailability(
            kind,
            Available: false,
            Verified: false,
            "测试环境没有沙箱。",
            Detail: null));

    public Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SandboxProbeReport> RunSelfTestAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WindowsSandboxSelfTestReport> RunWindowsSandboxSelfTestAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class NullAuditLog : IToolAuditLog
{
    public void Write(ToolAuditEntry entry)
    {
    }
}
