using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 成本档 1 的模型路由器：用一次小模型调用判断任务类型，并在「模型总控」模式下直接挑模型。
/// 只输出 JSON；候选模型由宿主提供，返回值还要被 <see cref="ModelRouter"/> 再校验一次，
/// 所以模型编造模型名、越权或格式不符都不会影响本轮可用性。
/// </summary>
public sealed class LlmModelRoutingAdvisor : IModelRoutingAdvisor
{
    /// <summary>路由结果缓存时长；输入预览与真正发送通常在同一输入上相隔几秒。</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    /// <summary>最多缓存多少条，避免长会话无限增长。</summary>
    public const int MaxCacheEntries = 64;

    internal const string SchemaJson = """
        {"type":"object","additionalProperties":false,"required":["task"],"properties":{
          "task":{"type":"string","enum":["chat","simple","code","complex"],"description":"本轮任务类型"},
          "provider":{"type":"string","description":"候选模型里的 provider id；不确定就省略"},
          "model":{"type":"string","description":"候选模型里的 model id；不确定就省略"},
          "reason":{"type":"string","description":"一句话理由，不超过 80 字"},
          "confidence":{"type":"number","minimum":0,"maximum":1}
        }}
        """;

    private const string SystemPrompt = """
        你是桌面 Agent 的「模型路由分类器」。你不执行用户请求，只判断这一轮该用哪一类模型。
        任务类型：
        - chat：寒暄、致谢、闲聊、身份询问，不需要工程能力。
        - simple：简短问答、解释概念、小范围只读查询，通常一步就能答完。
        - code：改代码、跑构建 / 测试、看日志、单文件或少量文件的工程任务。
        - complex：架构 / 方案 / 根因排查 / 跨文件重构 / 性能与安全 / 长上下文，需要更强推理。
        规则：
        1. 只输出一个 JSON 对象，不要 markdown 代码块，不要解释。
        2. 优先按任务难度而不是输入长度判断；短问题也可能很难。
        3. 如果给了候选模型，且本轮明显需要更强或更便宜的模型，可以填 provider + model；
           只能从候选里选，不确定时省略，宿主会按用户配置的类型映射选模型。
        4. 用户明确要求改代码 / 跑命令时不要判成 chat。
        5. reason 不超过 80 字，confidence 用 0–1 表示。
        """;

    private readonly IRoutingChatTransport _transport;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public LlmModelRoutingAdvisor(IRoutingChatTransport transport) => _transport = transport;

    public async Task<ModelRoutingAdvice?> AdviseAsync(
        ModelRoutingAdviceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserInput) || request.Candidates.Count == 0)
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
                request.RouterProvider,
                request.RouterModelId,
                SystemPrompt,
                BuildUserPrompt(request),
                SchemaJson,
                ModelRoutingSettings.AdvisorMaxOutputTokens,
                cancellationToken).ConfigureAwait(false);

            var advice = Parse(text, request.Candidates);
            if (advice is not null)
            {
                Remember(key, advice);
            }

            return advice;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 契约：除取消外，路由失败不抛异常，调用方回落规则。
            return null;
        }
    }

    /// <summary>解析模型输出；公开给测试与评测复用。未知任务 / 未知模型都收敛成 null 或去掉模型。</summary>
    public static ModelRoutingAdvice? Parse(
        string? text,
        IReadOnlyList<ModelRoutingCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(text))
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

            var taskText = ReadString(root, "task", 20);
            if (!TryParseTask(taskText, out var task))
            {
                return null;
            }

            var providerId = ReadString(root, "provider", 120);
            var modelId = ReadString(root, "model", 200);

            // 模型名必须在候选清单里；只给 model 时要求唯一匹配，否则丢掉模型只保留任务类型。
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var matches = candidates
                    .Where(candidate => string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => string.IsNullOrWhiteSpace(providerId)
                        || string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (matches.Length == 1)
                {
                    providerId = matches[0].ProviderId;
                    modelId = matches[0].ModelId;
                }
                else
                {
                    providerId = null;
                    modelId = null;
                }
            }
            else
            {
                providerId = null;
            }

            var confidence = 1.0;
            var confidenceNode = FindProperty(root, "confidence");
            if (confidenceNode is { ValueKind: JsonValueKind.Number }
                && confidenceNode.Value.TryGetDouble(out var parsed))
            {
                confidence = Math.Clamp(parsed, 0, 1);
            }

            return new ModelRoutingAdvice(
                task,
                providerId,
                modelId,
                ReadString(root, "reason", 160),
                confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>构造用户提示；独立出来便于测试「候选清单与成本偏好确实进了提示」。</summary>
    public static string BuildUserPrompt(ModelRoutingAdviceRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 用户请求");
        builder.AppendLine(request.UserInput.Trim());
        builder.AppendLine();

        builder.AppendLine($"## 规则初判\n{request.RuleTask.ToDisplayName()} · 复杂度 {request.Complexity}/100");
        builder.AppendLine($"## 成本偏好\n{CostPreferenceName(request.CostPreference)}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.RecentContext))
        {
            builder.AppendLine("## 最近对话（截断）");
            builder.AppendLine(request.RecentContext.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("## 候选模型（model 只能从这里选）");
        foreach (var candidate in request.Candidates)
        {
            builder.Append("- ")
                .Append(candidate.ProviderId)
                .Append(" / ")
                .Append(candidate.ModelId)
                .Append("（窗口 ")
                .Append(candidate.ContextWindow)
                .Append(" · ")
                .Append(candidate.SupportsTools ? "支持工具" : "无工具");

            if (candidate.RecommendedFor.Count > 0)
            {
                builder.Append(" · 已映射 ")
                    .Append(string.Join('/', candidate.RecommendedFor.Select(task => task.ToDisplayName())));
            }

            builder.Append('）');
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("## 输出格式（严格遵守 JSON Schema）");
        builder.AppendLine(SchemaJson.Trim());
        builder.AppendLine();
        builder.Append("示例：{\"task\":\"code\",\"reason\":\"要改代码并跑测试\",\"confidence\":0.88}");
        return builder.ToString();
    }

    private static string CostPreferenceName(ModelCostPreference preference) => preference switch
    {
        ModelCostPreference.Economy => "省钱：能完成就选便宜 / 小窗口模型",
        ModelCostPreference.Quality => "质量：优先强模型 / 大窗口模型",
        _ => "均衡：简单任务省钱，复杂任务升级"
    };

    private static bool TryParseTask(string? value, out ModelTaskKind task)
    {
        task = ModelTaskKind.Simple;
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value.Replace("-", string.Empty).Replace("_", string.Empty), true, out task);
    }

    private static string BuildCacheKey(ModelRoutingAdviceRequest request)
    {
        var candidates = string.Join(',', request.Candidates.Select(candidate =>
            candidate.ProviderId + "/" + candidate.ModelId));
        return $"{request.RuleTask}|{request.CostPreference}|{request.UserInput.Trim()}|{candidates}";
    }

    private void Remember(string key, ModelRoutingAdvice advice)
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

    private sealed record CacheEntry(ModelRoutingAdvice Advice, DateTimeOffset CreatedAt);
}
