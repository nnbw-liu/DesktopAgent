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
            [new AgentToolFunctionDescriptor("fake_tool", "假工具")]));

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
}

internal sealed class NullAuditLog : IToolAuditLog
{
    public void Write(ToolAuditEntry entry)
    {
    }
}
