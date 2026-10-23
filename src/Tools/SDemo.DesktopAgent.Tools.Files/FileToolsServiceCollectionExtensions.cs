using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>只读文件工具包注册。</summary>
public static class FileToolsServiceCollectionExtensions
{
    public static IServiceCollection AddFileTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, FileToolPack>();
        return services;
    }
}
