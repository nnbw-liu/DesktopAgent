namespace SDemo.DesktopAgent.Plugins.Summarize;

/// <summary>内置总结插件：声明式清单 + 技能，作为嵌入资源随程序集发布。</summary>
public sealed class SummarizePlugin()
    : EmbeddedPluginPackage("summarize", typeof(SummarizePlugin).Assembly);
