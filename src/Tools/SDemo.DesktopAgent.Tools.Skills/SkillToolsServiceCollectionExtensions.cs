using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>技能工具包注册（M2）。</summary>
public static class SkillToolsServiceCollectionExtensions
{
    public static IServiceCollection AddSkillTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, SkillToolPack>();
        return services;
    }
}
