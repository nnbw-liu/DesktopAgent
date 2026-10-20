using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SDemo.DesktopAgent.Services;

/// <summary>模型目录获取失败时的异常，Message 可直接显示给用户。</summary>
public sealed class ModelCatalogException(string message) : Exception(message);

/// <summary>从 OpenAI 兼容服务读取模型列表（GET {baseUrl}/models）。</summary>
public interface IModelCatalogService
{
    Task<IReadOnlyList<string>> GetModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ModelCatalogService : IModelCatalogService, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var url = BuildModelsUrl(baseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            throw new ModelCatalogException("请求超时，请检查 API 地址是否可访问。");
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCatalogException($"无法连接到 {url}：{ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelCatalogException(DescribeFailure((int)response.StatusCode, body));
            }

            return ParseModelIds(body);
        }
    }

    /// <summary>兼容 baseUrl 带不带 /v1 两种写法。</summary>
    internal static string BuildModelsUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            throw new ModelCatalogException("请先填写 API 地址。");
        }

        var uri = new Uri(trimmed, UriKind.Absolute);
        var path = uri.AbsolutePath.TrimEnd('/');

        // 只给了域名（或带 /v1）时补全标准 OpenAI 路径。
        if (path.Length == 0)
        {
            return trimmed + "/v1/models";
        }

        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/models";
    }

    internal static IReadOnlyList<string> ParseModelIds(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new ModelCatalogException("返回内容不是标准 OpenAI 模型列表（缺少 data 数组）。");
            }

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        ids.Add(value);
                    }
                }
            }

            if (ids.Count == 0)
            {
                throw new ModelCatalogException("服务端没有返回任何模型。");
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }
        catch (JsonException)
        {
            throw new ModelCatalogException("返回内容不是合法 JSON，请确认是标准 OpenAI API。");
        }
    }

    /// <summary>把常见 HTTP 错误翻译成对用户有用的提示。</summary>
    private static string DescribeFailure(int statusCode, string body) => statusCode switch
    {
        401 or 403 => $"API Key 无效或没有权限（HTTP {statusCode}）。",
        404 or 405 => "该服务没有提供模型列表接口（HTTP 404），请在下方手动添加模型 ID。"
                      + "例如火山方舟 Agent Plan 的 /api/plan/v3 就不支持 /models。",
        _ => $"获取模型失败（HTTP {statusCode}）：{Shorten(body)}"
    };

    private static string Shorten(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160] + "…";
    }

    public void Dispose() => _http.Dispose();
}
