using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Services;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;

namespace SDemo.DesktopAgent.Platform.Windows.Composition;

/// <summary>
/// Windows 宿主的沙箱服务注册（WPF 与 MAUI-Windows 共用）。
/// S1：真实 AppContainer profile / ACL / Job Object / 运行器 / 探针。
/// S4：Windows Sandbox 运行器（Pro / Enterprise / Education）。
/// S6：SandboxJanitor 的清理实现。
/// </summary>
public static class WindowsSandboxServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsSandbox(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SandboxDirectory(sp.GetRequiredService<IAppPaths>().SandboxRoot));
        services.AddSingleton<AppContainerProfileProvider>();
        services.AddSingleton<AppContainerSandboxRunner>();
        services.AddSingleton<WindowsSandboxRunner>();

        // 两个运行器都进 ISandboxRunner 集合，SandboxSelector 才能按策略挑；
        // 最后注册的 AppContainer 是 GetRequiredService<ISandboxRunner>() 的默认值。
        services.AddSingleton<ISandboxRunner>(sp => sp.GetRequiredService<WindowsSandboxRunner>());
        services.AddSingleton<ISandboxRunner>(sp => sp.GetRequiredService<AppContainerSandboxRunner>());

        services.AddSingleton<SandboxProbe>(sp => sp.GetRequiredService<AppContainerSandboxRunner>().Probe);

        services.AddSingleton<SandboxSelector>();
        services.AddSingleton<ISandboxSelector>(sp => sp.GetRequiredService<SandboxSelector>());

        services.AddSingleton<SandboxJobService>();
        services.AddSingleton<ISandboxJobService>(sp => sp.GetRequiredService<SandboxJobService>());

        services.AddSingleton<SandboxJanitor>();

        return services;
    }
}
