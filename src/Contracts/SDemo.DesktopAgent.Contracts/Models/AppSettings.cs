using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Models;

/// <summary>应用设置根对象，序列化为 %APPDATA%\SDemo\DesktopAgent\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>外观：跟随系统 / 浅色 / 深色。</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>界面语言（目前只实现 zh-CN）。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>
    /// 切换聊天室时是否让正在生成的回复继续在后台跑。
    /// 默认关闭：切走即中断，和旧行为一致。
    /// </summary>
    public bool ContinueGenerationOnChatSwitch { get; set; }

    /// <summary>模型提供商列表（只支持标准 OpenAI API）。</summary>
    public List<ModelProvider> Providers { get; set; } = [];

    /// <summary>上次使用的 provider（聊天输入框里的模型选择器）。</summary>
    public string? LastProviderId { get; set; }

    /// <summary>上次使用的模型。</summary>
    public string? LastModelId { get; set; }

    /// <summary>联网搜索配置（默认必应 RSS）。</summary>
    public SearchSettings Search { get; set; } = new();

    /// <summary>模型写文件前是否需要用户确认（默认需要）。</summary>
    public bool ConfirmFileWrite { get; set; } = true;

    /// <summary>工作区列表。</summary>
    public List<Workspace> Workspaces { get; set; } = [];

    /// <summary>插件设置（总开关、显示上限、每个插件的启用 / 入口 / 顺序）。</summary>
    public PluginPreferences Plugins { get; set; } = new();

    /// <summary>执行沙箱配置（S0 起接入；AppContainer / Windows Sandbox 分别在 S1 / S4 落地）。</summary>
    public SandboxSettings Sandbox { get; set; } = new();

    /// <summary>工具包开关（搜索 / 文件 / PowerShell 等，按 <c>AgentToolPackDescriptor.Id</c> 记录）。</summary>
    public ToolSettings Tools { get; set; } = new();

    /// <summary>上下文预算与裁剪（P1 起；旧文件没有该节点时按默认值）。</summary>
    public ContextSettings Context { get; set; } = new();

    /// <summary>直跑与命令白名单（P4 起；默认关闭）。</summary>
    public ExecutionSettings Execution { get; set; } = new();

    /// <summary>热工作区（P4 可选；默认关闭）。</summary>
    public WarmWorkspaceSettings WarmWorkspace { get; set; } = new();

    /// <summary>验证门与自动续跑（P5）。</summary>
    public SDemo.DesktopAgent.Execution.VerificationSettings Verification { get; set; } = new();

    /// <summary>编辑检查点（P5）。</summary>
    public SDemo.DesktopAgent.Execution.CheckpointSettings Checkpoints { get; set; } = new();

    /// <summary>小模型 / 工具面（P6）。</summary>
    public AgentSettings Agent { get; set; } = new();

    /// <summary>智能模型路由（默认关闭；旧设置文件没有该节点时按默认值）。</summary>
    public ModelRoutingSettings ModelRouting { get; set; } = new();

    /// <summary>对话呈现：过程展示档位与收起时机（P0）。</summary>
    public ChatPresentationSettings Chat { get; set; } = new();

    /// <summary>技能显示偏好（哪些技能在输入框里固定显示 chip）。</summary>
    public SkillPreferences Skills { get; set; } = new();
}
