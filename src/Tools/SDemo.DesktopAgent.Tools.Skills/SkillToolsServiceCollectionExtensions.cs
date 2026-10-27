using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SDemo.DesktopAgent.Services;

/// <summary>技能工具包注册（M2）；脚本执行器默认不可用，Windows 宿主会替换成真实实现。</summary>
public static class SkillToolsServiceCollectionExtensions
{
    public static IServiceCollection AddSkillTools(this IServiceCollection services)
    {
        services.TryAddSingleton<ISkillScriptRunner, UnavailableSkillScriptRunner>();
        services.AddSingleton<IAgentToolPack, SkillToolPack>();
        return services;
    }
}
