using System.ClientModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatMessageRole = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.P3Live;

/// <summary>
/// P3 真机任务：只读用户设置里的 provider / key，把工作区指向 P3_WORKSPACE，
/// 让模型用 files / edits 工具完成一次任务，并打印工具调用序列与改动摘要。
/// 运行：P3_WORKSPACE=... P3_PROMPT=... dotnet run --project tools/p3-live -c Release
/// 可选：P3_PROVIDER / P3_MODEL / P3_MAX_OUTPUT。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main()
    {
        var workspace = Environment.GetEnvironmentVariable("P3_WORKSPACE");
        var prompt = Environment.GetEnvironmentVariable("P3_PROMPT");
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            Console.Error.WriteLine("P3_WORKSPACE 必须是已存在的工作区目录。");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("P3_PROMPT 不能为空。");
            return 2;
        }

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent",
            "settings.json");
        var settings = LoadSettings(settingsPath);
        var providerId = Environment.GetEnvironmentVariable("P3_PROVIDER") ?? "火山";
        var source = settings.Providers.FirstOrDefault(item => item.Id == providerId)
            ?? throw new InvalidOperationException($"设置里没有 provider：{providerId}");
        var modelId = Environment.GetEnvironmentVariable("P3_MODEL")
            ?? source.Models.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"provider {providerId} 没有模型");

        var provider = new ModelProvider
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            BaseUrl = source.BaseUrl,
            ApiKey = source.ApiKey,
            Models = [new ModelEntry { Id = modelId, Profile = ModelProfilePresets.FromPreset("32K") }]
        };

        settings.Context.KeepRecentTurns = 4;
        settings.Context.SmallModelKeepRecentTurns = 4;
        settings.Context.StatePackEnabled = false;
        settings.Context.MidTurnTrim = true;
        settings.Context.AutoCompact = false;

        var settingsService = new InMemorySettings(settings);
        var builder = new ContextBuilder(new HeuristicTokenCounter(), settingsService);
        var registry = new AgentToolRegistry([new FileToolPack(), new EditToolPack()]);
        var approvals = new AutoApprovals();
        var paths = new StubPaths(Path.Combine(Path.GetTempPath(), "sdemo-p3-live"));
        var workspaceContext = new WorkspaceContext(paths) { RootPath = workspace };
        var chat = new AgentChatService(
            registry,
            workspaceContext,
            settingsService,
            approvals,
            new UnavailableSandboxJobService(),
            new NullAuditLog(),
            paths,
            new FileReadRegistry(), new SDemo.DesktopAgent.Agents.MafAgentRuntime());

        var metrics = new AgentRunMetrics();
        var messages = new List<ChatMessageModel>
        {
            new(ChatMessageRole.User, prompt)
        };

        var plan = builder.Build(new ContextBuildRequest(provider, modelId, messages));
        var runPlan = new AgentRunPlan(
            provider,
            modelId,
            plan,
            new HashSet<string>(StringComparer.Ordinal) { "files", "edits" },
            SessionId: "p3-live",
            Metrics: metrics);

        var output = new StringBuilder();
        var telemetry = new ContextRunTelemetry();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var delta in chat.RunAsync(runPlan, telemetry))
            {
                output.Append(delta.Text);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"运行失败：{ex.Message}");
            return 1;
        }

        stopwatch.Stop();
        Console.WriteLine("=== 模型输出 ===");
        Console.WriteLine(output.ToString());
        Console.WriteLine();
        Console.WriteLine($"=== 工具调用（{metrics.ToolTrace.Count} 条，耗时 {stopwatch.Elapsed.TotalSeconds:F1}s） ===");
        foreach (var entry in metrics.ToolTrace)
        {
            Console.WriteLine($"- {entry.ToolId} {entry.ArgsDigest} -> {entry.Status} {entry.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 审批 ===");
        foreach (var request in approvals.Requests)
        {
            Console.WriteLine($"- {request.ToolId}: {request.Summary} ({request.Detail.Length} 字符)");
        }

        Console.WriteLine();
        Console.WriteLine("=== 改动 ===");
        foreach (var file in metrics.ToolTrace.SelectMany(entry => entry.Edits ?? []))
        {
            Console.WriteLine($"- {file.Path} +{file.Added} -{file.Removed}");
        }

        Console.WriteLine();
        Console.WriteLine($"估算/预算：{telemetry.Report?.Estimated}/{telemetry.Report?.Total}");
        return 0;
    }

    private static AppSettings LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到设置文件：{path}");
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("设置文件解析失败");
        var entropy = Encoding.UTF8.GetBytes("SDemo.DesktopAgent.ApiKey.v1");

        foreach (var provider in settings.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKeyProtected))
            {
                continue;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(provider.ApiKeyProtected),
                    entropy,
                    DataProtectionScope.CurrentUser);
                provider.ApiKey = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Console.Error.WriteLine($"provider {provider.Id} 的 Key 无法解密。");
            }
        }

        return settings;
    }

    private sealed class InMemorySettings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; } = current;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void Save()
        {
        }
    }

    private sealed class AutoApprovals : IToolApprovalService
    {
        private readonly object _sync = new();
        private readonly List<ToolApprovalRequest> _requests = [];

        public IReadOnlyList<ToolApprovalRequest> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

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
            lock (_sync)
            {
                _requests.Add(request);
            }

            return Task.FromResult(ToolApprovalDecision.ApproveOnce);
        }

        public void Resolve(ToolApprovalDecision decision)
        {
        }

        public void CancelAll()
        {
        }
    }

    private sealed class NullAuditLog : IToolAuditLog
    {
        public void Write(ToolAuditEntry entry)
        {
        }
    }

    private sealed class StubPaths(string dataRoot) : IAppPaths
    {
        public string DataRoot { get; } = dataRoot;

        public string SettingsFile => Path.Combine(DataRoot, "settings.json");

        public string ChatsRoot => Path.Combine(DataRoot, "chats");

        public string UploadsRoot => Path.Combine(DataRoot, "uploads");

        public string SkillsRoot => Path.Combine(DataRoot, "skills");

        public string PluginsRoot => Path.Combine(DataRoot, "plugins");

        public string DefaultWorkspaceRoot => Path.Combine(DataRoot, "workspace");

        public string SandboxRoot => Path.Combine(DataRoot, "sandbox");
    }
}
