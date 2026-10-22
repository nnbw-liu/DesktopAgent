using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 平台无关的服务注册。两个宿主（WPF / MAUI）都调用它，再补齐自己的平台实现：
/// <see cref="IAppPaths"/>、<see cref="ISecureStore"/>、<see cref="ISystemThemeProvider"/>、
/// <see cref="IFolderPickerService"/>、<see cref="IAppEnvironmentService"/>。
/// </summary>
public static class AgentCoreServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopAgentCore(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IModelCatalogService, ModelCatalogService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IToolApprovalService, ToolApprovalService>();
        services.AddSingleton<IToolAuditLog, JsonlToolAuditLog>();
        services.AddSingleton<AgentToolRegistry>();
        services.AddSingleton<IAgentToolPack, SearchToolPack>();
        services.AddSingleton<IAgentToolPack, FileToolPack>();
        services.AddSingleton<IAgentToolPack, EditToolPack>();

        // P3：读前写注册表（按会话 + 2 小时 TTL）。
        services.AddSingleton<IFileReadRegistry, FileReadRegistry>();
        services.AddSingleton<SDemo.DesktopAgent.Execution.SessionGrantStore>();
        services.AddSingleton<SDemo.DesktopAgent.Execution.IWorkspaceWarmupService,
            SDemo.DesktopAgent.Execution.UnavailableWorkspaceWarmupService>();
        services.AddSingleton<WorkspaceContext>();
        services.AddSingleton<ChangeApplier>();
        services.AddSingleton<ChatStore>();
        services.AddSingleton<SkillCatalog>();

        // P1 上下文层：单一入口 + 计量 + 校准 + 溢出自愈。
        services.AddSingleton<ITokenCounter, HeuristicTokenCounter>();
        services.AddSingleton<TokenCalibrationStore>();
        services.AddSingleton<ContextBuilder>();
        services.AddSingleton<IAgentSessionRunner, AgentSessionRunner>();

        // P2 压缩与工作记忆：摘要（模型 + 兜底）、压缩编排、状态包。
        services.AddSingleton<IWorkspaceTreeScanner, FileSystemWorkspaceTreeScanner>();
        services.AddSingleton<WorkspaceStateProvider>();
        services.AddSingleton<ISummaryChatTransport, OpenAiSummaryChatTransport>();
        services.AddSingleton<IContextSummarizer, LlmContextSummarizer>();
        services.AddSingleton<ExtractiveContextSummarizer>();
        services.AddSingleton(sp => new ContextCompactor(
            sp.GetRequiredService<ITokenCounter>(),
            sp.GetRequiredService<IContextSummarizer>(),
            sp.GetRequiredService<ExtractiveContextSummarizer>(),
            (session, cancellationToken) =>
                sp.GetRequiredService<ChatStore>().SaveSessionAsync(session, cancellationToken)));

        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<PluginPromptComposer>();
        services.AddSingleton<PluginParameterResolver>();
        services.AddSingleton<IPluginRunner, PluginRunner>();
        services.AddSingleton<IAgentChatService, AgentChatService>();

        return services;
    }
}
