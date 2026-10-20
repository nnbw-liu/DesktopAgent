using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Plugins;

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
        services.AddSingleton<FileWriteApprovalService>();
        services.AddSingleton<WorkspaceContext>();
        services.AddSingleton<ChatStore>();
        services.AddSingleton<SkillCatalog>();
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<PluginPromptComposer>();
        services.AddSingleton<PluginParameterResolver>();
        services.AddSingleton<IPluginRunner, PluginRunner>();
        services.AddSingleton<AgentToolbox>();
        services.AddSingleton<IAgentChatService, AgentChatService>();

        return services;
    }
}
