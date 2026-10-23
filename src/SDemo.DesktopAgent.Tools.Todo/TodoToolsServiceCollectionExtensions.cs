using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>任务计划工具包注册。</summary>
public static class TodoToolsServiceCollectionExtensions
{
    public static IServiceCollection AddTodoTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, TodoToolPack>();
        return services;
    }
}
