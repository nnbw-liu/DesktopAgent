using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Models;

/// <summary>应用设置根对象，序列化为 %APPDATA%\SDemo\DesktopAgent\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>外观：跟随系统 / 浅色 / 深色。</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>界面语言（目前只实现 zh-CN）。</summary>
    public string Language { get; set; } = "zh-CN";

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
}
