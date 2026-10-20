using SDemo.DesktopAgent.Plugins;

namespace SDemo.DesktopAgent.Components.Sidebar;

/// <summary>侧栏入口的一条：插件（取图标与显示名）+ 它的 sidebar 入口。</summary>
public sealed record PluginRailEntry(PluginDescriptor Plugin, PluginMount Mount);
