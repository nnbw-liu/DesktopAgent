using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>工具层注册：注册表本身；具体工具包由各自的模块注册。</summary>
public static class ToolsServiceCollectionExtensions
{
    public static IServiceCollection AddAgentTooling(this IServiceCollection services)
    {
        services.AddSingleton<AgentToolRegistry>();
        return services;
    }
}
