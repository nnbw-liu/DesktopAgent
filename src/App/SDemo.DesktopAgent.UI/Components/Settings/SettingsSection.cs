namespace SDemo.DesktopAgent.Components.Settings;

/// <summary>
/// 设置页的分区。分区顺序与导航分组由 <see cref="SettingsView"/> 决定，这里只保留稳定标识。
/// </summary>
public enum SettingsSection
{
    /// <summary>外观、语言与对话表现。</summary>
    General,

    /// <summary>模型路由与提供商。</summary>
    Models,

    /// <summary>上下文预算、压缩与宿主快照。</summary>
    Context,

    /// <summary>联网搜索、工具包与文件安全。</summary>
    Tools,

    /// <summary>技能库。</summary>
    Skills,

    /// <summary>插件。</summary>
    Plugins,

    /// <summary>直跑命令、PowerShell、PDF、执行沙箱与审计。</summary>
    Sandbox,

    /// <summary>版本信息与本地数据。</summary>
    About
}
