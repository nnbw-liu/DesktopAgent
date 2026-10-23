using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>写入类工具包注册（edits + 小模型单工具 code_edit）。</summary>
public static class EditToolsServiceCollectionExtensions
{
    public static IServiceCollection AddEditTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, EditToolPack>();
        services.AddSingleton<IAgentToolPack, CodeEditToolPack>();
        return services;
    }
}
