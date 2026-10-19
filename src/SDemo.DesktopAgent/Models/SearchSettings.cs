using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Models;

/// <summary>联网搜索后端。</summary>
public enum SearchBackend
{
    /// <summary>必应 RSS（免密钥，默认）。</summary>
    Bing,

    /// <summary>自定义搜索 API（OpenAI 无关，按 query 调用）。</summary>
    Custom
}

/// <summary>联网搜索配置。API Key 与 provider 一样用 DPAPI 加密落盘。</summary>
public sealed class SearchSettings
{
    public SearchBackend Backend { get; set; } = SearchBackend.Bing;

    /// <summary>自定义搜索接口地址。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>运行期明文 Key，不写入 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 Key。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyProtected { get; set; }

    /// <summary>旧版明文，只用于迁移。</summary>
    [JsonPropertyName("apiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyApiKey { get; set; }
}
