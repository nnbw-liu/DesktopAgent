using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Context;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// M4 成本档 1：用一次小模型调用把「规则拿不准」的请求落成工具清单。
/// 只输出 JSON（tools / intent / reason / confidence），提示里带完整 JSON Schema 与可用工具；
/// 宿主再做一次白名单过滤，所以模型写错工具名、越权或格式不符都不会影响本轮可用性。
/// 任何失败（超时 / 网络 / JSON 非法 / 空结果）都返回 null 或由调用方按取消处理，
/// 最终都回落规则，不影响本轮可用性。
/// </summary>
public sealed class SmallModelRoutingAdvisor : IRoutingAdvisor
{
    /// <summary>路由结果缓存时长；预览与发送通常在同一输入上相隔几秒。</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    /// <summary>最多缓存多少条，避免长会话里无限增长。</summary>
    public const int MaxCacheEntries = 64;

    /// <summary>单次路由输出上限；只够放一小组工具名与一句理由。</summary>
    public const int MaxOutputTokens = 400;

    /// <summary>建议里最多保留多少个工具名（宿主还会按本轮上限截断）。</summary>
    public const int MaxAdvisedTools = 24;

    internal const string SchemaJson = """
        {"type":"object","additionalProperties":false,"required":["tools"],"properties":{
          "tools":{"type":"array","items":{"type":"string"},"description":"本轮需要暴露的工具 id，按优先级排序"},
          "intent":{"type":"string","description":"一句话意图标签，例如 edit / pdf / memory"},
          "reason":{"type":"string","description":"一句话理由，不超过 80 字"},
          "confidence":{"type":"number","minimum":0,"maximum":1}
        }}
        """;

    private const string SystemPrompt = """
        你是桌面 Agent 的「工具路由分类器」。你不执行用户请求，只判断这一轮需要哪些工具。
        规则：
        1. 只输出一个 JSON 对象，不要 markdown 代码块，不要解释，不要寒暄。
        2. tools 只能从「可选工具」里选，不能编造；最多选本轮上限个，按优先级排序。
        3. 跨会话回忆（上次 / 之前 / 归档）优先 search_workspace_memory；读 PDF 优先 pdf_*；
           构建 / 测试 / 查构建日志优先 run_command；只有用户明确要求 PowerShell / 沙箱 / 脚本执行时才选 run_powershell；
           联网找资料优先 web_search。
        4. 读文件、搜索、改代码是常用核心能力；但不要把所有工具都选上，只选确实需要的。
        5. 拿不准时少选，宿主会用规则补齐；confidence 用 0–1 表示。
        """;

    private readonly IRoutingChatTransport _transport;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public SmallModelRoutingAdvisor(IRoutingChatTransport transport) => _transport = transport;

    public async Task<RoutingAdvice?> AdviseAsync(
        RoutingAdviceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserInput)
            || string.IsNullOrWhiteSpace(request.ModelId)
            || request.Capabilities.Count == 0)
        {
            return null;
        }

        var key = BuildCacheKey(request);
        if (_cache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.CreatedAt < CacheLifetime)
        {
            return cached.Advice;
        }

        try
        {
            var text = await _transport.CompleteAsync(
                request.Provider,
                request.ModelId,
                SystemPrompt,
                BuildUserPrompt(request),
                SchemaJson,
                MaxOutputTokens,
                cancellationToken).ConfigureAwait(false);

            var advice = Parse(text, request.Capabilities);
            if (advice is not null)
            {
                Remember(key, advice);
            }

            return advice;
        }
        catch (OperationCanceledException)
        {
            // 超时 / 用户取消由宿主区分：调用方（Runner）会把超时回落成规则。
            throw;
        }
        catch (Exception)
        {
            // 契约：除取消外，路由失败不抛异常，调用方回落规则。
            return null;
        }
    }

    /// <summary>解析模型输出；公开给测试与评测复用。未知工具、空工具数组都视为无效建议。</summary>
    public static RoutingAdvice? Parse(string? text, IReadOnlyList<RoutingCapability> capabilities)
    {
        if (string.IsNullOrWhiteSpace(text) || capabilities.Count == 0)
        {
            return null;
        }

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var toolsNode = FindProperty(root, "tools");
            if (toolsNode is not { ValueKind: JsonValueKind.Array })
            {
                return null;
            }

            var byName = capabilities
                .GroupBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

            var tools = new List<string>();
            foreach (var item in toolsNode.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var requested = item.GetString()?.Trim();
                if (string.IsNullOrEmpty(requested)
                    || !byName.TryGetValue(requested, out var canonical)
                    || tools.Contains(canonical, StringComparer.Ordinal))
                {
                    continue;
                }

                tools.Add(canonical);
                if (tools.Count >= MaxAdvisedTools)
                {
                    break;
                }
            }

            if (tools.Count == 0)
            {
                return null;
            }

            var intent = ReadString(root, "intent", 40);
            var reason = ReadString(root, "reason", 160);
            var confidence = 1.0;
            var confidenceNode = FindProperty(root, "confidence");
            if (confidenceNode is { ValueKind: JsonValueKind.Number }
                && confidenceNode.Value.TryGetDouble(out var parsed))
            {
                confidence = Math.Clamp(parsed, 0, 1);
            }

            return new RoutingAdvice(tools, intent, reason, confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>构造用户提示；独立出来便于测试「提示里确实带了可用工具与上限」。</summary>
    public static string BuildUserPrompt(RoutingAdviceRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 用户请求");
        builder.AppendLine(request.UserInput.Trim());
        builder.AppendLine();
        builder.AppendLine($"## 本轮模式与上限\n{request.Mode} · 最多 {Math.Max(1, request.MaxExposedTools)} 个工具");
        builder.AppendLine();

        if (request.RecentToolIds.Count > 0)
        {
            builder.AppendLine("## 最近调用过的工具（优先复用）");
            builder.AppendLine(string.Join("、", request.RecentToolIds.Take(8)));
            builder.AppendLine();
        }

        if (request.Skills.Count > 0)
        {
            builder.AppendLine("## 可用技能（需要正文时选 load_skill，并在 reason 里写技能名）");
            builder.AppendLine(string.Join("、", request.Skills.Take(20)));
            builder.AppendLine();
        }

        builder.AppendLine("## 可选工具（只能从这里选）");
        foreach (var capability in request.Capabilities)
        {
            builder.Append("- ")
                .Append(capability.Id)
                .Append("（")
                .Append(capability.PackName)
                .Append("）：")
                .AppendLine(capability.Description);
        }

        builder.AppendLine();
        builder.AppendLine("## 输出格式（严格遵守 JSON Schema）");
        builder.AppendLine(SchemaJson.Trim());
        builder.AppendLine();
        builder.Append("示例：{\"tools\":[\"read_file\",\"apply_patch\"],\"intent\":\"edit\",\"reason\":\"需要先读再改\",\"confidence\":0.9}");
        return builder.ToString();
    }

    private static string BuildCacheKey(RoutingAdviceRequest request)
    {
        var capabilities = string.Join(',', request.Capabilities.Select(capability => capability.Id));
        return $"{request.ModelId}|{request.Mode}|{request.MaxExposedTools}|{request.UserInput.Trim()}|{capabilities}";
    }

    private void Remember(string key, RoutingAdvice advice)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            var oldest = _cache.OrderBy(pair => pair.Value.CreatedAt).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _cache.TryRemove(oldest.Key, out _);
            }
        }

        _cache[key] = new CacheEntry(advice, DateTimeOffset.UtcNow);
    }

    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            if (firstLine >= 0)
            {
                trimmed = trimmed[(firstLine + 1)..];
            }

            var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                trimmed = trimmed[..fence];
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }

    private static JsonElement? FindProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string name, int maxLength)
    {
        var node = FindProperty(root, name);
        if (node is not { ValueKind: JsonValueKind.String })
        {
            return null;
        }

        var value = node.Value.GetString()?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record CacheEntry(RoutingAdvice Advice, DateTimeOffset CreatedAt);
}
