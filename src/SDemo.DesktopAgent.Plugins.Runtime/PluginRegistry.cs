using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 插件运行时的「有效视图」：把目录扫描结果与用户设置（总开关、启用、侧栏 / 输入框开关、顺序）合并。
/// UI 只从这里取入口，所以改设置后立刻生效，既不需要重启也不需要重新扫描。
/// 优先级：用户显式设置 &gt; manifest 的 show / order &gt; 内置默认。
/// </summary>
public sealed class PluginRegistry
{
    private readonly PluginCatalog _catalog;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private Snapshot? _snapshot;

    public PluginRegistry(PluginCatalog catalog, ISettingsService settings)
    {
        _catalog = catalog;
        _settings = settings;

        _catalog.Changed += OnSourceChanged;
        _settings.Changed += OnSourceChanged;
    }

    /// <summary>目录重新扫描或设置改动后触发（UI 据此刷新入口与当前选中的插件）。</summary>
    public event EventHandler? Changed;

    public PluginPreferences Preferences => _settings.Current.Plugins;

    /// <summary>启用且可用的插件（会话图标、显示名、设置页用）。</summary>
    public IReadOnlyList<PluginDescriptor> Plugins => SnapshotOf().Plugins;

    /// <summary>某个界面上真正可用的入口，已按顺序排好、并按用户开关过滤。</summary>
    public IReadOnlyList<PluginMount> Mounts(PluginSurface surface) => SnapshotOf().Mounts(surface);

    public PluginDescriptor? Find(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        return SnapshotOf().Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));
    }

    /// <summary>按 <c>&lt;pluginId&gt;/&lt;mountId&gt;</c> 找可用入口；插件被停用 / 入口被关掉都返回 null。</summary>
    public PluginMount? FindMount(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : SnapshotOf().MountsByKey.GetValueOrDefault(key);

    /// <summary>显示名：插件被停用后仍然能显示（用于历史消息归属），找不到才退回 id。</summary>
    public string DisplayName(string pluginId) =>
        _catalog.Result.Find(pluginId)?.Name ?? pluginId;

    /// <summary>某个界面的直接显示上限（设置里夹到 1–20）。</summary>
    public int MaxFor(PluginSurface surface) => surface switch
    {
        PluginSurface.Sidebar => PluginPreferences.ClampLimit(Preferences.MaxInSidebar),
        PluginSurface.Composer => PluginPreferences.ClampLimit(Preferences.MaxInComposer),
        _ => int.MaxValue
    };

    /// <summary>丢弃缓存并通知 UI（重新扫描后由 Catalog.Changed 触发）。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Invalidate();

    private Snapshot SnapshotOf()
    {
        lock (_gate)
        {
            return _snapshot ??= Build();
        }
    }

    private Snapshot Build()
    {
        var preferences = Preferences;
        var plugins = new List<PluginDescriptor>();
        var ordered = new List<(PluginMount Mount, int Order, string PluginName)>();

        if (preferences.Enabled)
        {
            foreach (var plugin in _catalog.Result.Usable)
            {
                var entry = preferences.FindEntry(plugin.Id);
                if (entry?.Enabled == false)
                {
                    continue;
                }

                plugins.Add(plugin);

                foreach (var mount in plugin.Mounts)
                {
                    if (!mount.IsUsable || !mount.Show || !IsSurfaceAllowed(mount.Surface, entry))
                    {
                        continue;
                    }

                    ordered.Add((mount, entry?.Order ?? mount.Order, plugin.Name));
                }
            }
        }

        var bySurface = new Dictionary<PluginSurface, List<PluginMount>>();
        foreach (var group in ordered.GroupBy(item => item.Mount.Surface))
        {
            bySurface[group.Key] = group
                .OrderBy(item => item.Order)
                .ThenBy(item => item.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Mount.Id, StringComparer.Ordinal)
                .Select(item => item.Mount)
                .ToList();
        }

        var byKey = new Dictionary<string, PluginMount>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            byKey[item.Mount.Key] = item.Mount;
        }

        return new Snapshot(plugins, bySurface, byKey);
    }

    /// <summary>
    /// 某个界面是否可用。气泡只受插件级启用控制之外的 <c>inMessage</c> 开关；
    /// <b>侧栏依赖输入框</b>：侧栏入口只是「新建一个预选该插件的会话」，输入框入口关掉后
    /// 进去也看不到插件，留着只会让人找不到插件，所以一并隐藏。
    /// </summary>
    private static bool IsSurfaceAllowed(PluginSurface surface, PluginEntryPreferences? entry)
    {
        var inComposer = entry?.InComposer ?? true;

        return surface switch
        {
            PluginSurface.Sidebar => inComposer && (entry?.InSidebar ?? true),
            PluginSurface.Composer => inComposer,
            PluginSurface.Message => entry?.InMessage ?? true,
            _ => true
        };
    }

    private sealed record Snapshot(
        IReadOnlyList<PluginDescriptor> Plugins,
        IReadOnlyDictionary<PluginSurface, List<PluginMount>> BySurface,
        IReadOnlyDictionary<string, PluginMount> MountsByKey)
    {
        public IReadOnlyList<PluginMount> Mounts(PluginSurface surface) =>
            BySurface.TryGetValue(surface, out var mounts) ? mounts : [];
    }
}
