using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 一个模型提供商（兼容标准 OpenAI API 的服务）。
/// API Key 在内存里是明文，落盘时用 DPAPI 加密成 <see cref="ApiKeyProtected"/>。
/// </summary>
public sealed class ModelProvider
{
    /// <summary>ProviderId，例如 openai / deepseek / local。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称，为空时界面回退到 <see cref="Id"/>。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>OpenAI 兼容 BaseUrl，例如 https://api.openai.com/v1。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>运行期使用的明文 Key，不写入 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 Key（Base64），由 SettingsService 读写。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyProtected { get; set; }

    /// <summary>旧版本的明文字段，只用于把老设置迁移成密文。</summary>
    [JsonPropertyName("apiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyApiKey { get; set; }

    /// <summary>模型目录。</summary>
    public List<ModelEntry> Models { get; set; } = [];

    [JsonIgnore]
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

/// <summary>模型目录中的一项。</summary>
public sealed class ModelEntry
{
    /// <summary>模型 ID，调用时原样传给服务端。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>可选显示名称。</summary>
    public string? DisplayName { get; set; }

    [JsonIgnore]
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}
