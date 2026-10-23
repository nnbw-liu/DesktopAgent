namespace SDemo.DesktopAgent.Plugins.Translate;

/// <summary>内置翻译插件：声明式清单 + 技能，作为嵌入资源随程序集发布。</summary>
public sealed class TranslatePlugin()
    : EmbeddedPluginPackage("translate", typeof(TranslatePlugin).Assembly);
