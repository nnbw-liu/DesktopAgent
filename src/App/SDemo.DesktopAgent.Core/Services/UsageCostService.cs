using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一行成本汇总：某个 provider / model 的 token 用量与（可选）金额。
/// <see cref="Priced"/> 为 false 表示设置里没配价格，只统计 token。
/// </summary>
public sealed record UsageCostLine(
    string ProviderId,
    string ModelId,
    int Calls,
    int InputTokens,
    int OutputTokens,
    decimal? Cost,
    bool Priced);

/// <summary>设置页「用量与成本」的只读快照。</summary>
public sealed record UsageCostSummary(
    IReadOnlyList<UsageCostLine> Lines,
    int CallsWithoutProvider,
    int SessionsScanned)
{
    public static UsageCostSummary Empty { get; } = new([], 0, 0);

    public int TotalInputTokens => Lines.Sum(line => line.InputTokens);

    public int TotalOutputTokens => Lines.Sum(line => line.OutputTokens);

    public int TotalCalls => Lines.Sum(line => line.Calls) + CallsWithoutProvider;

    /// <summary>全部模型都配了价格时给出总额；缺价格时为 null（不拿 0 冒充）。</summary>
    public decimal? TotalCost => Lines.Any(line => !line.Priced)
        ? null
        : Lines.Sum(line => line.Cost ?? 0m);
}

/// <summary>
/// R1.4：按 provider / model 汇总助手消息的 token 成本。
/// 数据来自已落库的消息（<see cref="ChatMessage.ProviderId"/> / <see cref="ChatMessage.ModelId"/>），
/// 不额外写日志；价格在 <c>settings.json</c> 的模型条目里配置，未配置时只显示 token。
/// </summary>
public sealed class UsageCostService
{
    private readonly ChatStore _store;
    private readonly ISettingsService _settings;

    public UsageCostService(ChatStore store, ISettingsService settings)
    {
        _store = store;
        _settings = settings;
    }

    public async Task<UsageCostSummary> SummarizeAsync(
        IReadOnlyList<Workspace>? workspaces = null,
        CancellationToken cancellationToken = default)
    {
        var targets = workspaces ?? _settings.Current.Workspaces;
        var sessions = await _store.LoadAllAsync(targets, cancellationToken).ConfigureAwait(false);

        var groups = new Dictionary<(string ProviderId, string ModelId), (int Calls, int Input, int Output)>(
            ProviderModelComparer.Instance);
        var withoutProvider = 0;

        foreach (var message in sessions.SelectMany(session => session.Messages))
        {
            if (message.Role != ChatMessageRole.Assistant
                || (message.InputTokens == 0 && message.OutputTokens == 0))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(message.ProviderId) || string.IsNullOrWhiteSpace(message.ModelId))
            {
                // 老消息没有归属：只计入「未标注」条数，不瞎猜 provider。
                withoutProvider++;
                continue;
            }

            var key = (message.ProviderId.Trim(), message.ModelId.Trim());
            var current = groups.TryGetValue(key, out var value) ? value : (0, 0, 0);
            groups[key] = (current.Item1 + 1, current.Item2 + message.InputTokens, current.Item3 + message.OutputTokens);
        }

        var priced = BuildPriceIndex();
        var lines = groups
            .OrderByDescending(pair => pair.Value.Input + pair.Value.Output)
            .ThenBy(pair => pair.Key.ProviderId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.ModelId, StringComparer.Ordinal)
            .Select(pair =>
            {
                priced.TryGetValue(pair.Key, out var price);
                var line = new UsageCostLine(
                    pair.Key.ProviderId,
                    pair.Key.ModelId,
                    pair.Value.Calls,
                    pair.Value.Input,
                    pair.Value.Output,
                    price?.Cost(pair.Value.Input, pair.Value.Output),
                    price is not null);

                return line;
            })
            .ToList();

        return new UsageCostSummary(lines, withoutProvider, sessions.Count);
    }

    /// <summary>价格索引：provider / model 都匹配才计价。</summary>
    private Dictionary<(string ProviderId, string ModelId), Price> BuildPriceIndex()
    {
        var index = new Dictionary<(string, string), Price>(ProviderModelComparer.Instance);

        foreach (var provider in _settings.Current.Providers)
        {
            foreach (var model in provider.Models)
            {
                if (model.InputPricePerMillion is not decimal input
                    || model.OutputPricePerMillion is not decimal output)
                {
                    continue;
                }

                index[(provider.Id, model.Id)] = new Price(input, output);
            }
        }

        return index;
    }

    private sealed record Price(decimal InputPerMillion, decimal OutputPerMillion)
    {
        /// <summary>按每百万 token 计价；保留 4 位小数，够看账单量级。</summary>
        public decimal Cost(int inputTokens, int outputTokens) =>
            Math.Round(
                inputTokens / 1_000_000m * InputPerMillion + outputTokens / 1_000_000m * OutputPerMillion,
                4,
                MidpointRounding.AwayFromZero);
    }

    private sealed class ProviderModelComparer : IEqualityComparer<(string ProviderId, string ModelId)>
    {
        public static ProviderModelComparer Instance { get; } = new();

        public bool Equals((string ProviderId, string ModelId) x, (string ProviderId, string ModelId) y) =>
            string.Equals(x.ProviderId, y.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ModelId, y.ModelId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProviderId, string ModelId) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProviderId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ModelId));
    }
}
