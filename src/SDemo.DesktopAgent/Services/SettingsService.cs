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

    private readonly string _filePath;

    public SettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent");

        _filePath = Path.Combine(directory, "settings.json");
        Current = Load(directory);

        // 旧版本把 API Key 明文存成 apiKey，这里立刻迁移成 DPAPI 密文。
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
                    : SecretProtector.Protect(provider.ApiKey);
                provider.LegacyApiKey = null;
            }

            Current.Search.ApiKeyProtected = string.IsNullOrEmpty(Current.Search.ApiKey)
                ? null
                : SecretProtector.Protect(Current.Search.ApiKey);
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

    private static AppSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");

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

                    foreach (var provider in settings.Providers)
                    {
                        provider.ApiKey = !string.IsNullOrEmpty(provider.ApiKeyProtected)
                            ? SecretProtector.Unprotect(provider.ApiKeyProtected)
                            : provider.LegacyApiKey ?? string.Empty;
                    }

                    settings.Search.ApiKey = !string.IsNullOrEmpty(settings.Search.ApiKeyProtected)
                        ? SecretProtector.Unprotect(settings.Search.ApiKeyProtected)
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
