using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>联网搜索工具包注册。</summary>
public static class SearchToolsServiceCollectionExtensions
{
    public static IServiceCollection AddSearchTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, SearchToolPack>();
        return services;
    }
}
