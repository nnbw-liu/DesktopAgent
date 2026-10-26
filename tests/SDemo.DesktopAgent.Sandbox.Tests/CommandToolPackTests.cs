using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Tools;
using SDemo.DesktopAgent.Services;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>run_command：策略拦截、审批、会话授权、审计、验证结果。</summary>
public sealed class CommandToolPackTests
{
    [Fact]
    public async Task 直跑未开启时拒绝且不执行()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner();
        var tool = Tool(temp, runner, direct: false);

        var result = await InvokeAsync(tool.Context, tool.Pack, "run_command", ("command", "dotnet"), ("args", "build"));

        Assert.Contains("直跑未开启", result);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task 元字符在审批前被拒绝()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner();
        var tool = Tool(temp, runner);

        var result = await InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "build & del x"));

        Assert.Contains("shell", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task 拒绝审批不执行()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner();
        var tool = Tool(temp, runner);

        var invocation = InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "build"));
        await WaitUntilAsync(() => tool.Approvals.Current is not null);

        Assert.Equal(ToolApprovalKind.ProcessExecution, tool.Approvals.Current!.Kind);
        Assert.True(tool.Approvals.Current.AllowSessionRemember);
        tool.Approvals.Resolve(ToolApprovalDecision.Deny);

        Assert.Contains("拒绝", await invocation);
        Assert.Empty(runner.Calls);
        Assert.Contains(tool.Audit.Entries, entry => entry.Approval == "Deny" && entry.Mode == "direct");
    }

    [Fact]
    public async Task 本会话授权后第二次不再审批()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner();
        var tool = Tool(temp, runner);

        var first = InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "build"));
        await WaitUntilAsync(() => tool.Approvals.Current is not null);
        tool.Approvals.Resolve(ToolApprovalDecision.ApproveForSession);
        Assert.Contains("退出码：0", await first);

        var second = await InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "build"));

        Assert.Null(tool.Approvals.Current);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("退出码：0", second);
        Assert.Contains(tool.Audit.Entries, entry => entry.Approval == "SessionGrant");
    }

    [Fact]
    public async Task 高风险命令不提供会话授权()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner();
        var tool = Tool(temp, runner);

        var invocation = InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "clean"));
        await WaitUntilAsync(() => tool.Approvals.Current is not null);

        Assert.False(tool.Approvals.Current!.AllowSessionRemember);
        Assert.Equal(AgentToolRisk.High, tool.Approvals.Current.Risk);
        tool.Approvals.Resolve(ToolApprovalDecision.Deny);
        await invocation;
    }

    [Fact]
    public async Task 构建输出写入验证结果与状态包()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner
        {
            Result = new DirectRunResult(
                0,
                "Build succeeded.\n    0 Warning(s)\n    0 Error(s)",
                string.Empty,
                false,
                false,
                100,
                false,
                false)
        };
        var tool = Tool(temp, runner);
        var invocation = InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "build Demo.sln"), ("purpose", "build"));
        await WaitUntilAsync(() => tool.Approvals.Current is not null);
        tool.Approvals.Resolve(ToolApprovalDecision.ApproveOnce);
        await invocation;

        Assert.NotNull(tool.Context.Session!.LastVerification);
        Assert.True(tool.Context.Session.LastVerification!.Success);
        Assert.Contains("dotnet build Demo.sln", tool.Context.Session.LastVerification.Command);

        var state = new SDemo.DesktopAgent.Context.WorkspaceStateProvider().Build(temp.Root, tool.Context.Session);
        Assert.Contains("成功", state.LastVerification);
    }

    [Fact]
    public async Task 审计记录模式命令hash与目录()
    {
        using var temp = new P4TempDirectory();
        var runner = new FakeRunner();
        var tool = Tool(temp, runner);
        var invocation = InvokeAsync(tool.Context, tool.Pack, "run_command",
            ("command", "dotnet"), ("args", "build"));
        await WaitUntilAsync(() => tool.Approvals.Current is not null);
        tool.Approvals.Resolve(ToolApprovalDecision.ApproveOnce);
        await invocation;

        var audit = Assert.Single(tool.Audit.Entries);
        Assert.Equal("direct", audit.Mode);
        Assert.StartsWith("sha256:", audit.CommandSha256);
        Assert.Equal(".", audit.WorkingDirectory);
        Assert.Equal("ApproveOnce", audit.Approval);
    }

    [Fact]
    public async Task 工具包默认关闭()
    {
        using var temp = new P4TempDirectory();
        var settings = new P4StubSettings();
        settings.Current.Execution = new ExecutionSettings
        {
            Mode = ExecutionMode.AllowlistDirect,
            DirectRunEnabled = true
        };
        var audit = new P4StubAudit();
        var context = new ToolContext(
            "s1", temp.Root, [], settings, new ToolApprovalService(),
            new UnavailableSandboxJobService(), audit);
        var pack = new CommandToolPack(new FakeRunner(), new SessionGrantStore());

        var result = await InvokeAsync(context, pack, "run_command", ("command", "dotnet"), ("args", "build"));

        Assert.Contains("未启用", result);
    }

    private static ToolFixture Tool(P4TempDirectory temp, FakeRunner runner, bool direct = true)
    {
        var settings = new P4StubSettings();
        settings.Current.Execution = new ExecutionSettings
        {
            Mode = direct ? ExecutionMode.AllowlistDirect : ExecutionMode.SandboxFirst,
            DirectRunEnabled = direct
        };
        settings.Current.Tools.SetPackEnabled(CommandToolPack.PackId, true);
        var approvals = new ToolApprovalService();
        var audit = new P4StubAudit();
        var session = ChatSession.CreateEmpty();
        var context = new ToolContext(
            "s1", temp.Root, [], settings, approvals,
            new UnavailableSandboxJobService(), audit,
            Session: session);
        var grants = new SessionGrantStore();
        return new ToolFixture(new CommandToolPack(runner, grants), context, approvals, audit);
    }

    private static async Task<string> InvokeAsync(
        ToolContext context,
        IAgentToolPack pack,
        string name,
        params (string Key, object? Value)[] arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            pack.Create(context).Single(function => function.Name == name));
        var values = arguments.ToDictionary(pair => pair.Key, pair => pair.Value);
        return (await tool.InvokeAsync(new AIFunctionArguments(values)))?.ToString() ?? string.Empty;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("等待审批请求超时。");
    }

    private sealed record ToolFixture(
        CommandToolPack Pack,
        ToolContext Context,
        ToolApprovalService Approvals,
        P4StubAudit Audit);

    private sealed class FakeRunner : IDirectProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public DirectRunResult Result { get; init; } =
            new(0, "ok", string.Empty, false, false, 10, false, false);

        public Task<DirectRunResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            int maxOutputChars,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(Result);
        }
    }
}
