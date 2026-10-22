using System.Diagnostics;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Tools;
using SDemo.DesktopAgent.Services;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.P4Loop;

/// <summary>
/// P4 真机内循环测量：走 run_command 的完整策略 / 审批桩 / 直跑 / 验证解析路径，
/// 在真实仓库上跑 build + test，记录耗时。运行：
///   dotnet run --project tools/p4-loop -c Release -- F:\AI\Code\SDemo\DesktopAgent
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"工作区不存在：{root}");
            return 2;
        }

        var settings = new StubSettings();
        settings.Current.Execution = new ExecutionSettings
        {
            Mode = ExecutionMode.AllowlistDirect,
            DirectRunEnabled = true,
            BuildTimeoutSeconds = 600
        };
        settings.Current.Tools.SetPackEnabled(CommandToolPack.PackId, true);

        var audit = new MemoryAudit();
        var approvals = new AutoApprovals();
        var session = ChatSession.CreateEmpty();
        var context = new ToolContext(
            "p4-loop",
            root,
            [],
            settings,
            approvals,
            new UnavailableSandboxJobService(),
            audit,
            Session: session);

        var pack = new CommandToolPack(new DirectProcessRunner(), new SessionGrantStore());

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            var build = Stopwatch.StartNew();
            var buildResult = await pack.RunCommandAsync(
                context,
                "dotnet",
                "build SDemo.DesktopAgent.Wpf.slnf --no-restore -v minimal",
                ".",
                0,
                "build");
            build.Stop();
            var buildVerification = session.LastVerification;

            var test = Stopwatch.StartNew();
            var testResult = await pack.RunCommandAsync(
                context,
                "dotnet",
                "test tests/SDemo.DesktopAgent.Core.Tests/SDemo.DesktopAgent.Core.Tests.csproj --no-build -v minimal",
                ".",
                0,
                "test");
            test.Stop();

            Console.WriteLine($"--- 第 {iteration} 轮 ---");
            Console.WriteLine($"build: {build.Elapsed.TotalSeconds:0.00}s exit={buildVerification?.ExitCode}");
            Console.WriteLine(FirstLine(buildResult));
            Console.WriteLine($"test: {test.Elapsed.TotalSeconds:0.00}s");
            Console.WriteLine(FirstLine(testResult));
            Console.WriteLine($"验证: {session.LastVerification?.Format()}");
        }

        Console.WriteLine($"审批次数: {approvals.Requests}");
        Console.WriteLine($"审计条数: {audit.Entries.Count}");
        return 0;
    }

    private static string FirstLine(string value)
    {
        var line = value.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return line is null ? string.Empty : "  " + line.Trim();
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void Save()
        {
        }
    }

    private sealed class MemoryAudit : IToolAuditLog
    {
        public List<ToolAuditEntry> Entries { get; } = [];

        public void Write(ToolAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class AutoApprovals : IToolApprovalService
    {
        public int Requests { get; private set; }

        public ToolApprovalRequest? Current => null;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests++;
            // 首次请求模拟用户点「本会话允许」；后续同前缀由 SessionGrantStore 直接放行。
            return Task.FromResult(ToolApprovalDecision.ApproveForSession);
        }

        public void Resolve(ToolApprovalDecision decision)
        {
        }

        public void CancelAll()
        {
        }
    }
}
