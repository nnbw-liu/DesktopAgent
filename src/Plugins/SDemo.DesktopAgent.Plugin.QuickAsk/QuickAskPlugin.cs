namespace SDemo.DesktopAgent.Plugins.QuickAsk;

/// <summary>内置简单问答插件：跳过技能与工具，直接回答；可选择是否关联上下文。</summary>
public sealed class QuickAskPlugin()
    : EmbeddedPluginPackage("quick-ask", typeof(QuickAskPlugin).Assembly);
