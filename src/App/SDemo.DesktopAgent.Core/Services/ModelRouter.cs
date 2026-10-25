using System.Collections.Concurrent;
using System.Text;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 智能模型路由：手动固定 &lt; 规则 &lt; 混合 &lt; 模型总控。
/// <para>
/// 设计上刻意把「判断」和「配置」分开：规则 / 混合负责判断任务类型，模型由用户在设置里映射；
/// 总控模式才允许路由模型直接挑模型，而且候选清单、窗口、工具能力仍由宿主过滤。
/// 任何失败都回落规则，规则再失败回落当前聊天室模型，绝不因为路由让对话发不出去。
/// </para>
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    /// <summary>粘性决策的有效期；超过后即使任务类型相同也重新判断。</summary>
    public static readonly TimeSpan StickyLifetime = TimeSpan.FromMinutes(30);

    private readonly ITokenCounter _counter;
    private readonly IModelRoutingAdvisor? _advisor;
    private readonly ISettingsService? _settings;
    private readonly ConcurrentDictionary<string, StickyEntry> _sticky = new(StringComparer.Ordinal);

    public ModelRouter(
        ITokenCounter counter,
        IModelRoutingAdvisor? advisor = null,
        ISettingsService? settings = null)
    {
        _counter = counter;
        _advisor = advisor;
        _settings = settings;
    }

    public async Task<ModelRoutingDecision> RouteAsync(
        ModelRoutingRequest request,
        CancellationToken cancellationToken = default)
    {
        var estimatedTokens = EstimateInputTokens(request.Messages);
        var classification = ModelTaskClassifier.Classify(request, estimatedTokens);
        var allCandidates = BuildCandidates(request, classification.Task, estimatedTokens);

        // 手动固定 / 路由关闭：保持旧行为，但仍然把任务类型算出来用于界面解释。
        if (!request.IsAuto || request.Settings.Mode == ModelRoutingMode.Off)
        {
            return new ModelRoutingDecision(
                request.CurrentProvider,
                request.CurrentModelId,
                classification.Task,
                ModelRoutingSource.Manual,
                "手动选择模型",
                classification.Confidence,
                classification.Complexity);
        }

        // 粘性：同一会话、同一任务类型、同一份路由配置时沿用上一轮模型，减少提示缓存失效。
        var signature = BuildSignature(request.Settings);
        if (request.Settings.StickySession
            && !string.IsNullOrWhiteSpace(request.SessionId)
            && _sticky.TryGetValue(request.SessionId, out var sticky)
            && sticky.Decision.Task == classification.Task
            && string.Equals(sticky.Signature, signature, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow - sticky.At < StickyLifetime
            && FindCandidate(allCandidates, sticky.Decision.Provider.Id, sticky.Decision.ModelId) is not null)
        {
            return sticky.Decision with
            {
                Source = ModelRoutingSource.Sticky,
                Reason = $"同会话沿用上一轮模型（{classification.Task.ToDisplayName()}）",
                Confidence = classification.Confidence,
                Complexity = classification.Complexity
            };
        }

        // 输入框预览只做免费的规则判断：不要因为打开一个聊天室就调用路由模型。
        // 预览结果也不写粘性，真正发送时仍会按所选模式完整路由一次。
        if (!request.AllowAdvisor)
        {
            return RouteRules(request, classification, allCandidates) with
            {
                Reason = "预览 · " + classification.Reason
            };
        }

        ModelRoutingDecision decision;

        try
        {
            decision = request.Settings.Mode switch
            {
                ModelRoutingMode.Hybrid => await RouteHybridAsync(
                    request, classification, allCandidates, estimatedTokens, cancellationToken).ConfigureAwait(false),
                ModelRoutingMode.Model => await RouteModelAsync(
                    request, classification, allCandidates, estimatedTokens, cancellationToken).ConfigureAwait(false),
                _ => RouteRules(request, classification, allCandidates)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            decision = RouteRules(request, classification, allCandidates) with
            {
                Source = ModelRoutingSource.Fallback,
                Reason = "模型路由超时，回落规则"
            };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            decision = RouteRules(request, classification, allCandidates) with
            {
                Source = ModelRoutingSource.Fallback,
                Reason = "模型路由不可用，回落规则"
            };
        }

        if (request.Settings.StickySession && !string.IsNullOrWhiteSpace(request.SessionId))
        {
            _sticky[request.SessionId] = new StickyEntry(decision, signature, DateTimeOffset.UtcNow);
        }

        return decision;
    }

    public void ClearSession(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sticky.TryRemove(sessionId, out _);
        }
    }

    /// <summary>规则档：本地分类 + 用户映射，零额外请求。</summary>
    private static ModelRoutingDecision RouteRules(
        ModelRoutingRequest request,
        ModelTaskClassification classification,
        IReadOnlyList<CandidateContext> candidates) =>
        PickBound(request, classification, candidates);

    /// <summary>混合档：规则有把握就直接用；拿不准才让路由模型判断任务类型。</summary>
    private async Task<ModelRoutingDecision> RouteHybridAsync(
        ModelRoutingRequest request,
        ModelTaskClassification classification,
        IReadOnlyList<CandidateContext> candidates,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        if (classification.Confidence >= request.Settings.AdvisorThreshold
            || _advisor is null
            || candidates.Count == 0)
        {
            return PickBound(request, classification, candidates);
        }

        var (provider, modelId) = ResolveRouterTarget(request);
        var advice = await AskAsync(
            request, provider, modelId, classification, candidates, cancellationToken).ConfigureAwait(false);

        if (advice is null)
        {
            return PickBound(request, classification, candidates) with
            {
                Source = ModelRoutingSource.Fallback,
                Reason = "路由模型无有效结果，回落规则"
            };
        }

        var advised = classification with
        {
            Task = advice.Task,
            Confidence = advice.Confidence,
            Complexity = Math.Max(classification.Complexity, ComplexityFor(advice.Task)),
            Reason = string.IsNullOrWhiteSpace(advice.Reason)
                ? $"路由模型判定为{advice.Task.ToDisplayName()}"
                : advice.Reason!
        };

        // 模型改判了任务类型：按新类型重新过滤候选（工具能力 / 窗口），再按用户映射选。
        var advisedCandidates = BuildCandidates(request, advice.Task, estimatedTokens);
        var decision = PickBound(request, advised, advisedCandidates);
        return decision with
        {
            Source = ModelRoutingSource.Advisor,
            Reason = string.IsNullOrWhiteSpace(advice.Reason)
                ? decision.Reason
                : advice.Reason!
        };
    }

    /// <summary>总控档：路由模型从候选里直接选模型；闲聊仍走规则，不为一句寒暄多花一次调用。</summary>
    private async Task<ModelRoutingDecision> RouteModelAsync(
        ModelRoutingRequest request,
        ModelTaskClassification classification,
        IReadOnlyList<CandidateContext> candidates,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        if (classification.Task == ModelTaskKind.Chat || _advisor is null || candidates.Count == 0)
        {
            return PickBound(request, classification, candidates);
        }

        var (provider, modelId) = ResolveRouterTarget(request);
        var advice = await AskAsync(
            request, provider, modelId, classification, candidates, cancellationToken).ConfigureAwait(false);

        if (advice is null)
        {
            return PickBound(request, classification, candidates) with
            {
                Source = ModelRoutingSource.Fallback,
                Reason = "路由模型无有效结果，回落规则"
            };
        }

        var advisedTask = advice.Task;
        var advisedComplexity = Math.Max(classification.Complexity, ComplexityFor(advisedTask));
        var advisedCandidates = BuildCandidates(request, advisedTask, estimatedTokens);

        // 模型直接点名：只接受宿主候选清单里的组合。
        if (!string.IsNullOrWhiteSpace(advice.ModelId))
        {
            var chosen = FindCandidate(advisedCandidates, advice.ProviderId, advice.ModelId);
            if (chosen is not null)
            {
                return new ModelRoutingDecision(
                    chosen.Provider,
                    chosen.Entry.Id,
                    advisedTask,
                    ModelRoutingSource.Advisor,
                    string.IsNullOrWhiteSpace(advice.Reason) ? "路由模型选择" : advice.Reason!,
                    advice.Confidence,
                    advisedComplexity);
            }
        }

        // 只给了任务类型：按用户设置里这一类任务的映射选。
        var advised = classification with
        {
            Task = advisedTask,
            Confidence = advice.Confidence,
            Complexity = advisedComplexity,
            Reason = string.IsNullOrWhiteSpace(advice.Reason)
                ? $"路由模型判定为{advisedTask.ToDisplayName()}"
                : advice.Reason!
        };

        var decision = PickBound(request, advised, advisedCandidates);
        return decision with
        {
            Source = ModelRoutingSource.Advisor,
            Reason = string.IsNullOrWhiteSpace(advice.Reason) ? decision.Reason : advice.Reason!
        };
    }

    private async Task<ModelRoutingAdvice?> AskAsync(
        ModelRoutingRequest request,
        ModelProvider routerProvider,
        string routerModelId,
        ModelTaskClassification classification,
        IReadOnlyList<CandidateContext> candidates,
        CancellationToken cancellationToken)
    {
        if (_advisor is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.Settings.RouterTimeoutSeconds, 1, 60)));

        var adviceRequest = new ModelRoutingAdviceRequest(
            routerProvider,
            routerModelId,
            request.UserInput,
            BuildRecentContext(request.Messages),
            classification.Task,
            classification.Complexity,
            candidates
                .Take(ModelRoutingSettings.MaxAdvisorCandidates)
                .Select(candidate => new ModelRoutingCandidate(
                    candidate.Provider.Id,
                    candidate.Provider.Name,
                    candidate.Entry.Id,
                    candidate.Profile.ContextWindow,
                    candidate.Profile.SupportsTools,
                    candidate.Entry.InputPricePerMillion,
                    candidate.Entry.OutputPricePerMillion,
                    candidate.RecommendedFor))
                .ToArray(),
            request.Settings.CostPreference);

        return await _advisor.AdviseAsync(adviceRequest, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>按「任务映射 → 兜底映射 → 当前聊天室模型 → 候选第一项」选模型。</summary>
    private static ModelRoutingDecision PickBound(
        ModelRoutingRequest request,
        ModelTaskClassification classification,
        IReadOnlyList<CandidateContext> candidates)
    {
        var settings = request.Settings;
        var route = settings.Find(classification.Task);
        var source = ModelRoutingSource.Rules;
        var reason = $"按「{classification.Task.ToDisplayName()}」映射选择";

        if (route is not null)
        {
            var candidate = FindCandidate(candidates, route.ProviderId, route.ModelId);
            if (candidate is not null)
            {
                return Build(candidate, classification, source, reason);
            }

            reason = $"「{classification.Task.ToDisplayName()}」映射的模型不可用，已回落";
            source = ModelRoutingSource.Fallback;
        }

        if (settings.HasDefault)
        {
            var candidate = FindCandidate(candidates, settings.DefaultProviderId, settings.DefaultModelId);
            if (candidate is not null)
            {
                return Build(
                    candidate,
                    classification,
                    source,
                    route is null ? "未配置本类映射，使用兜底模型" : reason + "；使用兜底模型");
            }

            source = ModelRoutingSource.Fallback;
            reason = "兜底模型不可用，已回落当前聊天室模型";
        }

        var current = FindCandidate(candidates, request.CurrentProvider.Id, request.CurrentModelId);
        if (current is not null)
        {
            return Build(
                current,
                classification,
                source == ModelRoutingSource.Fallback ? ModelRoutingSource.Fallback : ModelRoutingSource.Rules,
                route is null && !settings.HasDefault ? "未配置映射，沿用当前模型" : reason);
        }

        var first = candidates[0];
        return Build(
            first,
            classification,
            ModelRoutingSource.Fallback,
            "配置的模型均不可用，使用当前可用模型");
    }

    private static ModelRoutingDecision Build(
        CandidateContext candidate,
        ModelTaskClassification classification,
        ModelRoutingSource source,
        string reason) =>
        new(
            candidate.Provider,
            candidate.Entry.Id,
            classification.Task,
            source,
            reason,
            classification.Confidence,
            classification.Complexity);

    /// <summary>
    /// 候选 = 全部已配置模型，按任务 / 窗口 / 工具能力过滤，再按成本偏好排序。
    /// 过滤后为空时放宽窗口限制，保证永远有一个可用模型。
    /// </summary>
    private static IReadOnlyList<CandidateContext> BuildCandidates(
        ModelRoutingRequest request,
        ModelTaskKind task,
        int estimatedTokens)
    {
        var all = new List<CandidateContext>();
        var recommended = request.Settings.Routes
            .Where(route => route.IsConfigured)
            .GroupBy(route => (route.ProviderId!, route.ModelId!), CandidateKeyComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => group.Select(route => route.Task).Distinct().ToArray(),
                CandidateKeyComparer.Instance);

        foreach (var provider in request.Providers)
        {
            foreach (var entry in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                var profile = ModelProfileResolver.ResolveConfigured(provider, entry.Id, entry);
                recommended.TryGetValue((provider.Id, entry.Id), out var tasks);

                all.Add(new CandidateContext(
                    provider,
                    entry,
                    profile,
                    tasks ?? []));
            }
        }

        if (all.Count == 0)
        {
            // 目录为空时仍然允许使用当前模型，至少不阻断对话。
            var fallbackProvider = request.CurrentProvider;
            var fallbackEntry = fallbackProvider.Models.FirstOrDefault(model =>
                string.Equals(model.Id, request.CurrentModelId, StringComparison.Ordinal))
                ?? new ModelEntry { Id = request.CurrentModelId };
            var profile = ModelProfileResolver.ResolveConfigured(fallbackProvider, request.CurrentModelId, fallbackEntry);

            return [new CandidateContext(fallbackProvider, fallbackEntry, profile, [])];
        }

        var filtered = all.AsEnumerable();

        // 复杂 / 代码任务优先排除明确不支持工具的模型，但只在还有别的选择时这么做。
        if (task is ModelTaskKind.Code or ModelTaskKind.Complex)
        {
            var withTools = filtered.Where(candidate => candidate.Profile.SupportsTools).ToArray();
            if (withTools.Length > 0)
            {
                filtered = withTools;
            }
        }

        if (request.Settings.RespectContextWindow && estimatedTokens > 0)
        {
            var required = estimatedTokens + 1024;
            var fitting = filtered
                .Where(candidate => candidate.Profile.ContextWindow * 0.9 >= required)
                .ToArray();

            if (fitting.Length == 0)
            {
                // 没有模型装得下：优先窗口最大的，让上下文层去做裁剪 / 压缩。
                filtered = all.OrderByDescending(candidate => candidate.Profile.ContextWindow);
            }
            else
            {
                filtered = fitting;
            }
        }

        return Sort(filtered, request.Settings.CostPreference, task).ToArray();
    }

    private static IEnumerable<CandidateContext> Sort(
        IEnumerable<CandidateContext> candidates,
        ModelCostPreference preference,
        ModelTaskKind task) =>
        preference switch
        {
            // 省钱：已映射到本类任务的优先，其次便宜的优先。未知价格排在已知价格之后。
            ModelCostPreference.Economy => candidates
                .OrderByDescending(candidate => candidate.RecommendedFor.Contains(task))
                .ThenBy(candidate => candidate.Entry.InputPricePerMillion ?? decimal.MaxValue)
                .ThenByDescending(candidate => candidate.Profile.ContextWindow),

            // 质量：大窗口 / 强模型优先，但仍让本类映射排在最前，尊重用户配置。
            ModelCostPreference.Quality => candidates
                .OrderByDescending(candidate => candidate.RecommendedFor.Contains(task))
                .ThenByDescending(candidate => candidate.Profile.ContextWindow)
                .ThenByDescending(candidate => candidate.Profile.SupportsReasoning)
                .ThenByDescending(candidate => candidate.Entry.OutputPricePerMillion ?? 0),

            // 均衡：映射优先，其次窗口与价格的折中。
            _ => candidates
                .OrderByDescending(candidate => candidate.RecommendedFor.Contains(task))
                .ThenByDescending(candidate => candidate.Profile.ContextWindow)
                .ThenBy(candidate => candidate.Entry.InputPricePerMillion ?? decimal.MaxValue)
        };

    private static CandidateContext? FindCandidate(
        IReadOnlyList<CandidateContext> candidates,
        string? providerId,
        string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Entry.Id, modelId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(providerId)
                || string.Equals(candidate.Provider.Id, providerId, StringComparison.Ordinal)));
    }

    /// <summary>路由模型：显式配置 → 摘要模型 → 当前会话模型。</summary>
    private (ModelProvider Provider, string ModelId) ResolveRouterTarget(ModelRoutingRequest request)
    {
        var context = _settings?.Current.Context;
        var providerId = string.IsNullOrWhiteSpace(request.Settings.RouterProviderId)
            ? context?.SummaryProviderId
            : request.Settings.RouterProviderId;
        var modelId = string.IsNullOrWhiteSpace(request.Settings.RouterModelId)
            ? context?.SummaryModelId
            : request.Settings.RouterModelId;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return (request.CurrentProvider, request.CurrentModelId);
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return (request.CurrentProvider, modelId);
        }

        var provider = request.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.Ordinal));

        return provider is null ? (request.CurrentProvider, request.CurrentModelId) : (provider, modelId);
    }

    private int EstimateInputTokens(IReadOnlyList<ChatMessage> messages)
    {
        var total = 200; // 系统提示与工具 schema 的粗略预留

        foreach (var message in messages)
        {
            total += _counter.CountMessage(message.Role.ToString(), message.Content);
        }

        return total;
    }

    private static string BuildRecentContext(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var message in messages.TakeLast(4))
        {
            var text = (message.Content ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > 400)
            {
                text = text[..400] + "…";
            }

            builder.Append(message.Role == ChatMessageRole.User ? "用户：" : "助手：")
                .AppendLine(text);
        }

        return builder.ToString();
    }

    private static int ComplexityFor(ModelTaskKind task) => task switch
    {
        ModelTaskKind.Chat => 5,
        ModelTaskKind.Simple => 25,
        ModelTaskKind.Code => 55,
        ModelTaskKind.Complex => 85,
        _ => 50
    };

    /// <summary>路由配置指纹：设置页改了映射 / 模式 / 成本偏好后，旧粘性决策立即失效。</summary>
    private static string BuildSignature(ModelRoutingSettings settings)
    {
        var routes = string.Join(';', settings.Routes
            .Where(route => route.IsConfigured)
            .OrderBy(route => route.Task)
            .Select(route => $"{route.Task}:{route.ProviderId}/{route.ModelId}"));

        return string.Join('|',
            settings.Mode,
            settings.CostPreference,
            settings.AdvisorThreshold,
            settings.RespectContextWindow,
            settings.DefaultProviderId,
            settings.DefaultModelId,
            settings.RouterProviderId,
            settings.RouterModelId,
            routes);
    }

    private sealed record CandidateContext(
        ModelProvider Provider,
        ModelEntry Entry,
        ModelProfile Profile,
        IReadOnlyList<ModelTaskKind> RecommendedFor);

    private sealed record StickyEntry(ModelRoutingDecision Decision, string Signature, DateTimeOffset At);

    private sealed class CandidateKeyComparer : IEqualityComparer<(string ProviderId, string ModelId)>
    {
        public static CandidateKeyComparer Instance { get; } = new();

        public bool Equals((string ProviderId, string ModelId) x, (string ProviderId, string ModelId) y) =>
            string.Equals(x.ProviderId, y.ProviderId, StringComparison.Ordinal)
            && string.Equals(x.ModelId, y.ModelId, StringComparison.Ordinal);

        public int GetHashCode((string ProviderId, string ModelId) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ProviderId),
                StringComparer.Ordinal.GetHashCode(obj.ModelId));
    }
}
