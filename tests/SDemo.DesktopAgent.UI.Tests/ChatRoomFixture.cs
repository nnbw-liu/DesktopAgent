using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Core.Services;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>内存版设置服务：不动真实 settings.json。</summary>
internal sealed class InMemorySettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public int SaveCount { get; private set; }

    public void Save()
    {
        SaveCount++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>插件运行器占位：组件测试不会真的跑插件。</summary>
internal sealed class NoPluginRunner : IPluginRunner
{
    public IAsyncEnumerable<AgentDelta> RunComposerAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default) =>
        Empty();

    public IAsyncEnumerable<AgentDelta> RunArtifactAsync(
        PluginRunRequest request,
        CancellationToken cancellationToken = default) =>
        Empty();

    private static async IAsyncEnumerable<AgentDelta> Empty()
    {
        await Task.Yield();
        yield break;
    }
}

/// <summary>知识库占位：组件测试不访问 FTS5。</summary>
internal sealed class NoMemory : IWorkspaceMemory
{
    public bool IsAvailable(string? workspaceId) => false;

    public int CountArchived(string? workspaceId) => 0;

    public IReadOnlyList<MemorySessionCard> ListArchived(string? workspaceId) => [];

    public MemorySearchResult Search(string? workspaceId, string query, int topK) =>
        MemorySearchResult.Empty(query, 0);

    public MemoryReadResult Read(
        string? workspaceId,
        string sessionId,
        string? query,
        int offset,
        int maxChars) =>
        new(false, sessionId, string.Empty, DateTime.MinValue, "知识库不可用", 0);

    public void Index(string? workspaceId, string sessionId)
    {
    }

    public void Remove(string? workspaceId, string sessionId)
    {
    }

    public void Rebuild(string? workspaceId)
    {
    }
}

internal sealed class NoAudit : IToolAuditLog
{
    public void Write(ToolAuditEntry entry)
    {
    }
}

/// <summary>ChatRoom 的依赖图：bUnit 测试与截图基线共用一份，避免两处漂移。</summary>
internal static class ChatRoomFixture
{
    public static void Register(
        TestContext context,
        ISettingsService settings,
        IAgentSessionRunner runner,
        string root)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var paths = new StubAppPaths(root);
        var store = new ChatStore(paths.ChatsRoot);
        var workspace = new WorkspaceContext(paths) { RootPath = root };
        var memory = new NoMemory();
        var audit = new NoAudit();
        var catalog = new PluginCatalog([], paths.PluginsRoot);

        context.Services.AddSingleton(settings);
        context.Services.AddSingleton(store);
        context.Services.AddSingleton<UsageCostService>();
        context.Services.AddSingleton<IVerificationRunner>(new UnavailableVerificationRunner());
        context.Services.AddSingleton<IWorkspaceWarmupService>(new UnavailableWorkspaceWarmupService());
        context.Services.AddSingleton(runner);
        context.Services.AddSingleton<IToolApprovalService>(new ToolApprovalService());
        context.Services.AddSingleton(new AgentToolRegistry([]));
        context.Services.AddSingleton<ISandboxJobService>(new UnavailableSandboxJobService());
        context.Services.AddSingleton(new SkillCatalog(paths.SkillsRoot));
        // P0.5：输入框 @ 补全的路径索引（真实实现，指向测试用的临时工作区）。
        context.Services.AddSingleton<IComposerReferenceIndex>(new WorkspaceReferenceIndex());
        context.Services.AddSingleton(new PluginRegistry(catalog, settings));
        context.Services.AddSingleton(new PluginParameterResolver());
        context.Services.AddSingleton<IPluginRunner>(new NoPluginRunner());
        context.Services.AddSingleton(workspace);
        context.Services.AddSingleton(new SessionArchiveService(store, memory));
        context.Services.AddSingleton<IWorkspaceMemory>(memory);
        context.Services.AddSingleton(new CheckpointStore(Path.Combine(paths.DataRoot, "checkpoints"), audit));
        context.Services.AddSingleton(new AttachmentStore(paths.UploadsRoot));
        context.Services.AddSingleton(new ChangeApplier());
        // 组件测试不起进程：打开 / 定位文件走兜底实现。
        context.Services.AddSingleton<IFileLauncherService>(new NullFileLauncherService());
    }

    /// <summary>给设置页的模型目录填一个可用模型（组件渲染需要非空列表）。</summary>
    public static void SeedProvider(AppSettings settings)
    {
        settings.Providers.Add(new ModelProvider
        {
            Id = "test",
            DisplayName = "本地",
            BaseUrl = "https://example.com/v1",
            ApiKey = "k",
            Models = [new ModelEntry { Id = "qwen3.5-4b", Profile = new ModelProfile(8192, 2048) }]
        });
        settings.LastProviderId = "test";
        settings.LastModelId = "qwen3.5-4b";
    }
}
