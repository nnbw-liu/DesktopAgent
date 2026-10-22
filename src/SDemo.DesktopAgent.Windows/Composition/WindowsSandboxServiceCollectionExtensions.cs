using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Services;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Platform.Windows.Tools;

namespace SDemo.DesktopAgent.Platform.Windows.Composition;

/// <summary>
/// Windows 宿主的沙箱服务注册（WPF 与 MAUI-Windows 共用）。
/// S1：真实 AppContainer profile / ACL / Job Object / 运行器 / 探针。
/// S4：Windows Sandbox 运行器（Pro / Enterprise / Education）。
/// S5：PDF 工具包与附件文本提取（解析固定在 SandboxHost / AppContainer）。
/// S6：SandboxJanitor 的清理实现。
/// </summary>
public static class WindowsSandboxServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsSandbox(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SandboxDirectory(sp.GetRequiredService<IAppPaths>().SandboxRoot));
        services.AddSingleton<AppContainerProfileProvider>();
        services.AddSingleton<AppContainerSandboxRunner>();

        // S4：Windows Sandbox。启动器单独注册，运行器用显式工厂构造，避免依赖 Func<> 的自动解析；
        // 测试可以直接 new WindowsSandboxRunner(fakeLauncher, fakeHostInfo) 注入假实现。
        services.AddSingleton<IWindowsSandboxLauncher, WindowsSandboxProcessLauncher>();
        services.AddSingleton(sp => new WindowsSandboxRunner(
            sp.GetRequiredService<IWindowsSandboxLauncher>(),
            WindowsSandboxAvailability.ReadHostInfo,
            sp.GetRequiredService<SandboxDirectory>()));

        // 两个运行器都进 ISandboxRunner 集合，SandboxSelector 才能按策略挑；
        // 最后注册的 AppContainer 是 GetRequiredService<ISandboxRunner>() 的默认值。
        services.AddSingleton<ISandboxRunner>(sp => sp.GetRequiredService<WindowsSandboxRunner>());
        services.AddSingleton<ISandboxRunner>(sp => sp.GetRequiredService<AppContainerSandboxRunner>());

        services.AddSingleton<SandboxProbe>(sp => sp.GetRequiredService<AppContainerSandboxRunner>().Probe);

        services.AddSingleton<SandboxSelector>();
        services.AddSingleton<ISandboxSelector>(sp => sp.GetRequiredService<SandboxSelector>());

        services.AddSingleton<SandboxHostInstaller>();
        services.AddSingleton<SandboxJobService>();
        services.AddSingleton<ISandboxJobService>(sp => sp.GetRequiredService<SandboxJobService>());

        services.AddSingleton<SandboxJanitor>();
        services.AddSingleton<ISandboxMaintenance>(sp => sp.GetRequiredService<SandboxJanitor>());

        // S3：PowerShell 工具包。只有 Windows 宿主注册；Android 连这个工程都不引用。
        services.AddSingleton<IAgentToolPack, PowerShellToolPack>();

        // P4：直跑白名单命令（run_command）+ 会话级授权。
        services.TryAddSingleton<SessionGrantStore>();
        services.AddSingleton<SDemo.DesktopAgent.Platform.Windows.Execution.IDirectProcessRunner,
            SDemo.DesktopAgent.Platform.Windows.Execution.DirectProcessRunner>();
        services.AddSingleton<IAgentToolPack, CommandToolPack>();
        services.AddSingleton<SDemo.DesktopAgent.Execution.IWorkspaceWarmupService,
            SDemo.DesktopAgent.Platform.Windows.Execution.WorkspaceWarmupService>();

        // S5：PDF 工具包与附件文本提取。解析在 SandboxHost（沙箱内）完成，宿主只负责编排。
        services.AddSingleton<IAgentToolPack, PdfToolPack>();
        services.AddSingleton<IPdfAttachmentExtractor, SandboxPdfAttachmentExtractor>();

        return services;
    }
}
