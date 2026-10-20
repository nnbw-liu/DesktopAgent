namespace SDemo.DesktopAgent.Models;

/// <summary>新会话译文默认呈现方式（双语对照在 M6 生效）。</summary>
public enum PluginOutputMode
{
    Bilingual,
    Translation
}

/// <summary>
/// 插件全局设置 + 每个插件的用户覆盖，持久化在 settings.json 的 <c>plugins</c> 节点。
/// 旧设置文件没有这个节点时走这里的默认值，不需要迁移代码。
/// </summary>
public sealed class PluginPreferences
{
    /// <summary>总开关：关掉后侧栏、输入框、气泡都不显示插件。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>输入框最多直接显示几个插件入口。</summary>
    public int MaxInComposer { get; set; } = 3;

    /// <summary>侧栏最多直接显示几个插件入口。</summary>
    public int MaxInSidebar { get; set; } = 6;

    /// <summary>是否按会话记住上次选的参数值。</summary>
    public bool RememberParameterValues { get; set; } = true;

    /// <summary>新会话译文默认呈现方式。</summary>
    public PluginOutputMode DefaultOutputMode { get; set; } = PluginOutputMode.Bilingual;

    /// <summary>
    /// 用户改过设置的插件条目。只在用户动过开关时写入；
    /// 插件被删除后条目保留，重装即恢复。
    /// </summary>
    public List<PluginEntryPreferences> Entries { get; set; } = [];

    /// <summary>已经确认过「文本会发给模型服务商」的插件 id（每个插件只提示一次）。</summary>
    public List<string> PrivacyAcknowledged { get; set; } = [];

    public PluginEntryPreferences? FindEntry(string? pluginId) =>
        string.IsNullOrWhiteSpace(pluginId)
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.Id, pluginId, StringComparison.Ordinal));

    /// <summary>取条目；没有就新建并加入列表（调用方负责 Save）。</summary>
    public PluginEntryPreferences GetOrAddEntry(string pluginId)
    {
        var entry = FindEntry(pluginId);
        if (entry is null)
        {
            entry = new PluginEntryPreferences { Id = pluginId };
            Entries.Add(entry);
        }

        return entry;
    }

    /// <summary>上限夹到 1–20，避免 0 或离谱数字把界面搞空。</summary>
    public static int ClampLimit(int value) => Math.Clamp(value, 1, 20);
}

/// <summary>单个插件的用户覆盖；null 表示「没设过，用 manifest 默认」。</summary>
public sealed class PluginEntryPreferences
{
    public string Id { get; set; } = string.Empty;

    /// <summary>启用开关（null = 启用）。</summary>
    public bool? Enabled { get; set; }

    /// <summary>侧栏入口开关（null = 用 manifest 的 show）。注意：输入框入口关闭时侧栏会跟着不可用。</summary>
    public bool? InSidebar { get; set; }

    /// <summary>输入框入口开关（null = 用 manifest 的 show）。</summary>
    public bool? InComposer { get; set; }

    /// <summary>气泡（消息底部按钮）开关（null = 用 manifest 的 show）。</summary>
    public bool? InMessage { get; set; }

    /// <summary>排序覆盖（null = 用 manifest 的 order）。</summary>
    public int? Order { get; set; }
}
