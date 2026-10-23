using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>工作区知识库工具包注册（M3）。</summary>
public static class MemoryToolsServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, MemoryToolPack>();
        return services;
    }
}
