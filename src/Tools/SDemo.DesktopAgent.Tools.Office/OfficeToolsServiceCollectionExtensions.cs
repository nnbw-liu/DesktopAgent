using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>Office 工具包与附件提取器注册。</summary>
public static class OfficeToolsServiceCollectionExtensions
{
    public static IServiceCollection AddOfficeTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolPack, OfficeToolPack>();
        services.AddSingleton<IOfficeAttachmentExtractor, OfficeAttachmentExtractor>();
        return services;
    }
}
