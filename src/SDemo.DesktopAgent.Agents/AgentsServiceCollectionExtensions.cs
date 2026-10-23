using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Agents;

/// <summary>AI 接入层注册：运行时 + 摘要传输 + 模型目录。</summary>
public static class AgentsServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopAgentAgents(this IServiceCollection services)
    {
        services.AddSingleton<IAgentRuntime, MafAgentRuntime>();
        // 摘要与模型目录也走 OpenAI 兼容协议，和主链路共享同一层依赖。
        services.AddSingleton<ISummaryChatTransport, OpenAiSummaryChatTransport>();
        services.AddSingleton<IModelCatalogService, ModelCatalogService>();
        return services;
    }
}
