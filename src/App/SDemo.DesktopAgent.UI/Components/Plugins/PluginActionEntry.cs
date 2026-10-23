using SDemo.DesktopAgent.Plugins;

namespace SDemo.DesktopAgent.Components.Plugins;

/// <summary>气泡上的一个插件按钮：插件 + 入口 + 本次解析出的参数值（会话记忆优先）。</summary>
public sealed record PluginActionEntry(
    PluginDescriptor Plugin,
    PluginMount Mount,
    IReadOnlyDictionary<string, string> Values);
