using System.IO;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 插件目录扫描器：先扫内置目录，再扫用户目录，然后在内存里排序、去重、
/// 让本地插件覆盖同 id 的内置插件。任何解析异常都在这里被吃掉，
/// 绝不让一个坏插件把异常抛到 UI 线程（一个坏插件只能降级它自己）。
/// </summary>
public sealed class PluginCatalog
{
    private readonly object _gate = new();
    private readonly PluginValidator _validator;
    private PluginLoadResult? _result;

    public PluginCatalog(IAppPaths paths)
        : this(DefaultBuiltinRoot(), paths.PluginsRoot)
    {
    }

    public PluginCatalog(string builtinRoot, string localRoot)
        : this(builtinRoot, localRoot, PluginHostVersion.Current)
    {
    }

    public PluginCatalog(string builtinRoot, string localRoot, PluginHostVersion hostVersion)
    {
        BuiltinRoot = builtinRoot;
        LocalRoot = localRoot;
        _validator = new PluginValidator(hostVersion);
    }

    /// <summary>内置插件目录（随应用发布，只读）。</summary>
    public string BuiltinRoot { get; }

    /// <summary>用户插件目录。</summary>
    public string LocalRoot { get; }

    /// <summary>扫描结果变化时触发（重新扫描、或首次扫描完成）。</summary>
    public event EventHandler? Changed;

    /// <summary>最近一次扫描结果；还没扫过就扫一次。</summary>
    public PluginLoadResult Result
    {
        get
        {
            lock (_gate)
            {
                return _result ??= ScanCore();
            }
        }
    }

    /// <summary>立刻重新扫描（设置页「重新扫描」、启动时预热都走这里）。</summary>
    public PluginLoadResult Scan()
    {
        PluginLoadResult result;
        lock (_gate)
        {
            result = ScanCore();
            _result = result;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>只在还没扫过时扫一次。</summary>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_result is not null)
            {
                return;
            }

            _result = ScanCore();
        }
    }

    /// <summary>内置插件默认放在 exe 同级的 <c>plugins</c> 目录（构建时从仓库复制）。</summary>
    public static string DefaultBuiltinRoot() => Path.Combine(AppContext.BaseDirectory, "plugins");

    private PluginLoadResult ScanCore()
    {
        var failures = new List<PluginFailure>();
        var builtin = ScanRoot(BuiltinRoot, PluginSource.Builtin, failures);
        var local = ScanRoot(LocalRoot, PluginSource.Local, failures);
        return Merge(builtin, local, failures, DateTimeOffset.Now);
    }

    private List<PluginDescriptor> ScanRoot(string root, PluginSource source, List<PluginFailure> failures)
    {
        var plugins = new List<PluginDescriptor>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            // 目录不存在（还没装过插件）不是错误：界面按「没有插件」处理。
            return plugins;
        }

        List<string> directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(root)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new PluginFailure(
                root,
                source,
                null,
                [new PluginIssue(PluginIssueSeverity.Error, $"插件目录无法读取：{ex.Message}")]));
            return plugins;
        }

        foreach (var directory in directories)
        {
            // 只认 plugins/<目录>/plugin.json；其它目录直接跳过（可能只是用户放杂物的地方）。
            if (!File.Exists(Path.Combine(directory, PluginManifestJson.FileName)))
            {
                continue;
            }

            var (plugin, failure) = LoadOne(directory, source);
            if (plugin is not null)
            {
                plugins.Add(plugin);
            }

            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        return plugins;
    }

    private (PluginDescriptor? Plugin, PluginFailure? Failure) LoadOne(string directory, PluginSource source)
    {
        var manifestPath = Path.Combine(directory, PluginManifestJson.FileName);

        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > PluginManifestJson.MaxBytes)
            {
                return (null, Fail(directory, source, null,
                    $"plugin.json 超过 {PluginManifestJson.MaxBytes / 1024} KB 上限（{info.Length} 字节）"));
            }

            // UTF-8（带不带 BOM 都接受）由 File.ReadAllText 默认处理。
            var json = File.ReadAllText(manifestPath);
            var parse = PluginManifestJson.Parse(json);
            if (!parse.Success)
            {
                return (null, Fail(directory, source, null, parse.Issues));
            }

            // 未知字段只记警告，不阻断加载。
            var unknownMembers = PluginManifestJson.FindUnknownMembers(json);

            var validation = _validator.Build(parse.Manifest!, directory, source);
            if (!validation.Success)
            {
                return (null, Fail(directory, source, parse.Manifest!.Id, validation.FatalIssues));
            }

            var plugin = validation.Plugin!;
            if (unknownMembers.Count > 0)
            {
                plugin = plugin with { Issues = [.. plugin.Issues, .. unknownMembers] };
            }

            return (plugin, null);
        }
        catch (Exception ex)
        {
            // 兜底：任何没预料到的异常也只影响这一个插件。
            return (null, Fail(directory, source, null, $"插件加载失败：{ex.Message}"));
        }
    }

    private PluginLoadResult Merge(
        List<PluginDescriptor> builtin,
        List<PluginDescriptor> local,
        List<PluginFailure> failures,
        DateTimeOffset scannedAt)
    {
        var plugins = new List<PluginDescriptor>();

        foreach (var group in builtin.Concat(local).GroupBy(plugin => plugin.Id, StringComparer.Ordinal))
        {
            // 本地优先；同源时按 order / 名称决定谁生效。
            var entries = group
                .OrderBy(plugin => plugin.Source == PluginSource.Local ? 0 : 1)
                .ThenBy(plugin => plugin.Order)
                .ThenBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var winner = entries[0];
            plugins.Add(winner);

            foreach (var entry in entries.Skip(1))
            {
                if (entry.Source == PluginSource.Builtin)
                {
                    // 内置插件被同 id 的本地插件覆盖：保留一条标记，设置页标注「已覆盖」。
                    plugins.Add(entry with { IsOverridden = true });
                }
                else
                {
                    plugins.Add(entry with
                    {
                        State = PluginState.Duplicate,
                        Issues =
                        [
                            .. entry.Issues,
                            new PluginIssue(
                                PluginIssueSeverity.Error,
                                $"插件 id \"{entry.Id}\" 与 {winner.DirectoryName} 重复，已忽略",
                                PluginManifestJson.FileName,
                                Path: "id")
                        ]
                    });
                }
            }
        }

        var sorted = plugins
            .OrderBy(plugin => plugin.Source)
            .ThenBy(plugin => plugin.Order)
            .ThenBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PluginLoadResult(sorted, failures, scannedAt, [BuiltinRoot, LocalRoot]);
    }

    private static PluginFailure Fail(string directory, PluginSource source, string? pluginId, params string[] messages) =>
        new(directory, source, pluginId, messages.Select(message => new PluginIssue(
            PluginIssueSeverity.Error,
            message,
            PluginManifestJson.FileName)).ToList());

    private static PluginFailure Fail(
        string directory,
        PluginSource source,
        string? pluginId,
        IReadOnlyList<PluginIssue> issues) =>
        new(directory, source, pluginId, issues);
}
