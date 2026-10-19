using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>联网搜索。默认走必应 RSS（免密钥），也可以在设置里换成自定义搜索 API。</summary>
public interface ISearchService
{
    Task<string> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SearchService : ISearchService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public SearchService(ISettingsService settings) => _settings = settings;

    public Task<string> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult("搜索关键词为空。");
        }

        var config = _settings.Current.Search;

        return config.Backend == SearchBackend.Custom
            ? SearchCustomAsync(query, config, cancellationToken)
            : SearchBingAsync(query, cancellationToken);
    }

    // ---------- 必应 RSS ----------

    private async Task<string> SearchBingAsync(string query, CancellationToken cancellationToken)
    {
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&format=rss";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"搜索失败：HTTP {(int)response.StatusCode}";
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xml);
            var items = document.Root?.Element("channel")?.Elements("item").Take(5).ToList() ?? [];

            if (items.Count == 0)
            {
                return "没有找到相关结果。";
            }

            var builder = new StringBuilder();
            var index = 1;

            foreach (var item in items)
            {
                var title = item.Element("title")?.Value?.Trim() ?? string.Empty;
                var link = item.Element("link")?.Value?.Trim() ?? string.Empty;
                var description = StripHtml(item.Element("description")?.Value ?? string.Empty);

                builder.AppendLine($"{index++}. {title}");
                builder.AppendLine($"   {link}");
                if (description.Length > 0)
                {
                    builder.AppendLine($"   {Trim(description, 240)}");
                }
            }

            return Trim(builder.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return $"搜索失败：{ex.Message}";
        }
    }

    // ---------- 自定义搜索 API ----------

    private async Task<string> SearchCustomAsync(string query, SearchSettings config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            return "搜索接口未配置，请到「设置 → 工具设置」填写地址。";
        }

        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "搜索接口地址不是合法的 http/https 地址。";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
            }

            var payload = JsonSerializer.Serialize(new { query, top_k = 5 });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return $"搜索失败（HTTP {(int)response.StatusCode}）：{Trim(body, 300)}";
            }

            return FormatCustomResponse(body);
        }
        catch (TaskCanceledException)
        {
            return "搜索超时，请检查搜索接口是否可访问。";
        }
        catch (HttpRequestException ex)
        {
            return $"无法连接搜索接口：{ex.Message}";
        }
    }

    /// <summary>自定义接口的返回结构未知，做通用提取：错误优先，其次找第一个对象数组。</summary>
    internal static string FormatCustomResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("ResponseMetadata", out var metadata) &&
                metadata.TryGetProperty("Error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var message = GetString(error, "Message");
                var code = GetString(error, "Code");
                return $"搜索失败：{message ?? code ?? "服务端返回错误"}";
            }

            var items = FindItemArray(document.RootElement, depth: 0);

            if (items is null || items.Value.GetArrayLength() == 0)
            {
                return Trim(json, 800);
            }

            var builder = new StringBuilder();
            var index = 1;

            foreach (var item in items.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = GetString(item, "title", "name", "Title", "Name");
                var url = GetString(item, "url", "link", "Url", "Link", "href");
                var snippet = GetString(item, "snippet", "summary", "description", "content", "Snippet", "Summary", "Description");

                builder.AppendLine($"{index++}. {title ?? "(无标题)"}");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    builder.AppendLine($"   {url}");
                }
                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    builder.AppendLine($"   {Trim(StripHtml(snippet), 240)}");
                }

                if (index > 5)
                {
                    break;
                }
            }

            return builder.Length == 0 ? Trim(json, 800) : Trim(builder.ToString());
        }
        catch (JsonException)
        {
            return "搜索接口返回的不是 JSON：" + Trim(json, 300);
        }
    }

    private static JsonElement? FindItemArray(JsonElement element, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 0 &&
                    element[0].ValueKind == JsonValueKind.Object)
                {
                    return element;
                }

                return null;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("ResponseMetadata"))
                    {
                        continue;
                    }

                    var found = FindItemArray(property.Value, depth + 1);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }

    private static string StripHtml(string text) =>
        Regex.Replace(text, "<[^>]+>", " ").Replace("&nbsp;", " ").Trim();

    private static string Trim(string text, int max = 800) =>
        text.Length <= max ? text : text[..max] + "\n…（已截断）";

    public void Dispose() => _http.Dispose();
}
