using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <inheritdoc />
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ISecureStore _secureStore;
    private readonly string _filePath;

    public SettingsService(IAppPaths paths, ISecureStore secureStore)
    {
        _secureStore = secureStore;
        _filePath = paths.SettingsFile;
        Current = Load(_filePath);

        // 旧版本把 API Key 明文存成 apiKey，这里立刻迁移成密文。
        if (Current.Providers.Any(provider => !string.IsNullOrEmpty(provider.LegacyApiKey)))
        {
            Save();
        }
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Save()
    {
        try
        {
            foreach (var provider in Current.Providers)
            {
                provider.ApiKeyProtected = string.IsNullOrEmpty(provider.ApiKey)
                    ? null
                    : _secureStore.Protect(provider.ApiKey);
                provider.LegacyApiKey = null;
            }

            Current.Search.ApiKeyProtected = string.IsNullOrEmpty(Current.Search.ApiKey)
                ? null
                : _secureStore.Protect(Current.Search.ApiKey);
            Current.Search.LegacyApiKey = null;

            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Current, JsonOptions));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置写入失败不影响当前会话，界面上仍保留本次修改。
        }
    }

    private AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    settings.Providers ??= [];
                    settings.Search ??= new SearchSettings();

                    // 旧 settings.json 没有 plugins 节点（或写成 null）时用默认值，不做迁移。
                    settings.Plugins ??= new PluginPreferences();
                    settings.Plugins.Entries ??= [];
                    settings.Plugins.PrivacyAcknowledged ??= [];

                    // 旧 settings.json 没有 sandbox 节点时按默认（Required + AppContainer）处理。
                    settings.Sandbox ??= new SandboxSettings();

                    // 旧 settings.json 没有 tools 节点（或写成 null）时用默认值：新增工具包默认关闭。
                    settings.Tools ??= new ToolSettings();
                    settings.Tools.EnabledPacks ??= [];

                    // 旧 settings.json 没有 context 节点时按默认预算处理，零迁移。
                    settings.Context ??= new ContextSettings();

                    // agent 节点（工具预算 / 小模型）同样兜底，避免 JSON 里写成 null。
                    settings.Agent ??= new AgentSettings();

                    // 智能模型路由：旧设置没有该节点时默认关闭；映射列表兜底为 []。
                    settings.ModelRouting ??= new ModelRoutingSettings();
                    settings.ModelRouting.Routes ??= [];

                    foreach (var provider in settings.Providers)
                    {
                        provider.ApiKey = !string.IsNullOrEmpty(provider.ApiKeyProtected)
                            ? _secureStore.Unprotect(provider.ApiKeyProtected)
                            : provider.LegacyApiKey ?? string.Empty;
                    }

                    settings.Search.ApiKey = !string.IsNullOrEmpty(settings.Search.ApiKeyProtected)
                        ? _secureStore.Unprotect(settings.Search.ApiKeyProtected)
                        : settings.Search.LegacyApiKey ?? string.Empty;

                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置文件损坏时回退到默认值，并备份原文件便于排查。
            TryBackup(path);
        }

        return new AppSettings();
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".bak", overwrite: true);
            }
        }
        catch (IOException)
        {
            // 备份失败无妨。
        }
    }
}
