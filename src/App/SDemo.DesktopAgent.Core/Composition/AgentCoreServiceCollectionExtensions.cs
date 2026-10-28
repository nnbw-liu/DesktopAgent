using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SDemo.DesktopAgent.Agents;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Core.Services;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Plugins;
using SDemo.DesktopAgent.Plugins.QuickAsk;
using SDemo.DesktopAgent.Plugins.Summarize;
using SDemo.DesktopAgent.Plugins.Translate;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 平台无关的服务注册。两个宿主（WPF / MAUI）都调用它，再补齐自己的平台实现：
/// <see cref="IAppPaths"/>、<see cref="ISecureStore"/>、<see cref="ISystemThemeProvider"/>、
/// <see cref="IFolderPickerService"/>、<see cref="IAppEnvironmentService"/>。
/// </summary>
public static class AgentCoreServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopAgentCore(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        // 打开 / 定位文件：默认兜底实现，平台宿主注册真实 Shell 后再覆盖。
        services.TryAddSingleton<IFileLauncherService, NullFileLauncherService>();
        services.AddDesktopAgentAgents();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IToolApprovalService, ToolApprovalService>();
        services.AddSingleton<IToolAuditLog, JsonlToolAuditLog>();

        // 工具层：注册表 + 各内置工具包（每个包一个模块，按需引用）。
        services.AddAgentTooling();
        services.AddSearchTools();
        services.AddFileTools();
        services.AddOfficeTools();
        services.AddEditTools();
        services.AddTodoTools();
        services.AddSkillTools();
        services.AddMemoryTools();

        // P3：读前写注册表（按会话 + 2 小时 TTL）。
        services.AddSingleton<IFileReadRegistry, FileReadRegistry>();
        services.AddSingleton<SDemo.DesktopAgent.Editing.CheckpointStore>();
        services.AddSingleton<SDemo.DesktopAgent.Execution.SessionGrantStore>();
        services.AddSingleton<SDemo.DesktopAgent.Execution.IWorkspaceWarmupService,
            SDemo.DesktopAgent.Execution.UnavailableWorkspaceWarmupService>();
        services.AddSingleton<SDemo.DesktopAgent.Execution.IVerificationRunner,
            SDemo.DesktopAgent.Execution.UnavailableVerificationRunner>();
        services.AddSingleton<WorkspaceContext>();
        services.AddSingleton<ChangeApplier>();
        services.AddSingleton<ChatStore>();
        // R1.4：按 provider / model 汇总 token 成本（设置页只读展示）。
        services.AddSingleton<UsageCostService>();
        services.AddSingleton<SkillCatalog>();
        // 内置技能：启动时把随应用发布的技能包补齐到技能库（带版本闸，幂等）。
        services.AddSingleton(sp => new BundledSkillInstaller(sp.GetRequiredService<SkillCatalog>()));
        // P3：Archify 3.2.0 安装入口（复制到技能库 + 适配 SKILL.md + 许可声明）。
        services.AddSingleton<IArchifyInstaller, ArchifyInstaller>();
        // P4：隔离预览与用户显式触发的视觉复核。平台宿主可覆盖预览 host。
        services.TryAddSingleton<IArtifactPreviewHost, UnavailableArtifactPreviewHost>();
        services.AddSingleton<IArtifactPreviewService, ArtifactPreviewService>();
        services.AddSingleton<IArchifyReviewService, ArchifyReviewService>();
        // M2：技能库的契约视图，供工具包（load_skill）与上下文层共用。
        services.AddSingleton<ISkillLibrary>(sp => sp.GetRequiredService<SkillCatalog>());
        // P0.5：输入框 `@` 补全的工作区路径索引（只给路径、30 秒缓存）。
        services.AddSingleton<IComposerReferenceIndex, WorkspaceReferenceIndex>();

        // M3：工作区知识库索引 + 归档编排（索引落在各工作区 DB 的 FTS5 表里）。
        services.AddSingleton<IWorkspaceMemory>(sp =>
            new WorkspaceMemoryIndex(sp.GetRequiredService<ChatStore>()));
        services.AddSingleton<SessionArchiveService>();

        // P1 上下文层：单一入口 + 计量 + 校准 + 溢出自愈。
        services.AddSingleton<ITokenCounter, HeuristicTokenCounter>();
        services.AddSingleton<TokenCalibrationStore>();
        services.AddSingleton<ContextBuilder>();
        // M4：规则不确定时的小模型路由；失败自动回落规则。
        services.AddSingleton<IRoutingAdvisor, SmallModelRoutingAdvisor>();
        // 智能模型路由（工具路由之上的一层）：规则 → 混合 → 模型总控，失败回落手动模型。
        services.AddSingleton<IModelRoutingAdvisor, LlmModelRoutingAdvisor>();
        services.AddSingleton<IModelRouter, ModelRouter>();
        services.AddSingleton<IAgentSessionRunner, AgentSessionRunner>();

        // P2 压缩与工作记忆：摘要（模型 + 兜底）、压缩编排、状态包。
        services.AddSingleton<IWorkspaceTreeScanner, FileSystemWorkspaceTreeScanner>();
        services.AddSingleton<WorkspaceStateProvider>();
        services.AddSingleton<IContextSummarizer, LlmContextSummarizer>();
        services.AddSingleton<ExtractiveContextSummarizer>();
        services.AddSingleton(sp => new ContextCompactor(
            sp.GetRequiredService<ITokenCounter>(),
            sp.GetRequiredService<IContextSummarizer>(),
            sp.GetRequiredService<ExtractiveContextSummarizer>(),
            (session, cancellationToken) =>
                sp.GetRequiredService<ChatStore>().SaveSessionAsync(session, cancellationToken)));

        // 内置插件：每个插件一个类库，清单与技能作为嵌入资源随程序集发布；
        // 本地插件仍是 %APPDATA%\...\plugins 下的目录，同 id 覆盖内置。
        services.AddSingleton<IPluginPackage, TranslatePlugin>();
        services.AddSingleton<IPluginPackage, SummarizePlugin>();
        services.AddSingleton<IPluginPackage, QuickAskPlugin>();
        services.AddSingleton(sp => new PluginCatalog(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetServices<IPluginPackage>()));
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<PluginPromptComposer>();
        services.AddSingleton<PluginParameterResolver>();
        services.AddSingleton<IPluginRunner, PluginRunner>();
        services.AddSingleton<IAgentChatService, AgentChatService>();

        return services;
    }
}
