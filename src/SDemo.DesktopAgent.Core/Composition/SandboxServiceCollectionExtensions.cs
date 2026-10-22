using Microsoft.Extensions.DependencyInjection;

namespace SDemo.DesktopAgent.Services;

/// <summary>没有 Windows 沙箱能力的平台（Android）使用的注册骨架。</summary>
public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// 注册“沙箱不可用”的实现：高风险工具会看到 NotAvailable 并拒绝执行，
    /// 不会因为缺少沙箱而退化成在主进程里直接跑 PowerShell。
    /// </summary>
    public static IServiceCollection AddUnavailableSandbox(this IServiceCollection services)
    {
        services.AddSingleton<ISandboxRunner, UnavailableSandboxRunner>();
        services.AddSingleton<ISandboxJobService, UnavailableSandboxJobService>();
        services.AddSingleton<ISandboxMaintenance, UnavailableSandboxMaintenance>();

        return services;
    }
}
