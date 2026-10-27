using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Tools;
using SDemo.DesktopAgent.Services;

namespace SDemo.RoutingEval;

/// <summary>
/// M4 路由评测：离线跑规则（tier 0），可选在线跑小模型（tier 1）。
/// 每个 case 记录 exposes / missing / forbidden，命中 = 期望工具全部暴露且禁用工具未暴露。
/// 用法：
///   dotnet run --project tools/routing-eval -c Release -- --mode rules
///   dotnet run --project tools/routing-eval -c Release -- --mode live --provider ds --model deepseek-flash
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        var cases = LoadCases(options.CasesPath)
            .Where(item => options.Only.Count == 0 || options.Only.Contains(item.Id))
            .ToArray();

        if (cases.Length == 0)
        {
            Console.Error.WriteLine("没有匹配的评测用例。");
            return 1;
        }

        var registry = CreateRegistry();
        var (provider, modelId, advisor) = options.Mode == "live"
            ? LoadLiveRouter(options)
            : (null, null, null);

        var results = new List<CaseResult>();

        foreach (var testCase in cases)
        {
            var packs = new HashSet<string>(testCase.Packs, StringComparer.Ordinal);
            var available = registry.ToolNames(packs);
            var mode = Enum.TryParse<AgentMode>(testCase.Mode, ignoreCase: true, out var parsedMode)
                ? parsedMode
                : AgentMode.Full;
            var settings = new AgentSettings();
            var maxTools = settings.ResolveMaxExposedTools(testCase.Window, mode);

            // P1b：历史轮（旧→新）用来判「任务档」。没有历史时与 M4 的判定完全一致。
            var previousInputs = testCase.History is { Length: > 0 } history
                ? CapabilityRouter.PreviousUserInputs([.. history, testCase.Prompt])
                : null;

            var input = new CapabilityRoutingInput(
                testCase.Prompt,
                [],
                available,
                mode,
                maxTools,
                EscapeHatch: true,
                ExposeAll: false,
                PreviousUserInputs: previousInputs);

            var rule = CapabilityRouter.Route(input);
            var final = rule;
            RoutingAdvice? advice = null;
            string? adviceError = null;
            long advisorMs = 0;

            var shouldAsk = advisor is not null
                            && mode == AgentMode.Full
                            && (CapabilityRouter.ShouldConsultAdvisor(input, rule)
                                || (options.AlwaysAdvise && rule.Truncated));

            if (shouldAsk)
            {
                var capabilities = BuildCapabilities(registry, packs);
                var request = new RoutingAdviceRequest(
                    provider!,
                    modelId!,
                    testCase.Prompt,
                    [],
                    mode,
                    maxTools,
                    capabilities,
                    []);
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(options.TimeoutSeconds));
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    advice = await advisor!.AdviseAsync(request, timeout.Token);
                }
                catch (Exception ex)
                {
                    adviceError = ex.Message;
                }

                stopwatch.Stop();
                advisorMs = stopwatch.ElapsedMilliseconds;

                if (advice is null)
                {
                    adviceError ??= "无有效 JSON / 工具建议";
                }
                else
                {
                    final = CapabilityRouter.ApplyAdvice(input, advice, rule);
                }
            }

            var expected = testCase.ExpectedTools;
            var missing = expected
                .Where(name => !final.ExposedTools.Contains(name, StringComparer.Ordinal))
                .ToArray();
            var forbidden = testCase.ForbiddenTools
                .Where(name => final.ExposedTools.Contains(name, StringComparer.Ordinal))
                .ToArray();
            var ruleMissing = expected
                .Where(name => !rule.ExposedTools.Contains(name, StringComparer.Ordinal))
                .ToArray();
            var ruleForbidden = testCase.ForbiddenTools
                .Where(name => rule.ExposedTools.Contains(name, StringComparer.Ordinal))
                .ToArray();

            results.Add(new CaseResult(
                testCase.Id,
                testCase.Prompt,
                testCase.Note,
                mode.ToString(),
                testCase.Window,
                maxTools,
                testCase.Packs,
                expected,
                final.ExposedTools.ToArray(),
                missing,
                final.ExposedTools
                    .Where(name => !expected.Contains(name, StringComparer.Ordinal))
                    .ToArray(),
                forbidden,
                final.Source,
                final.Reason,
                rule.Source,
                rule.ExposedTools.ToArray(),
                ruleMissing.Length == 0 && ruleForbidden.Length == 0,
                missing.Length == 0 && forbidden.Length == 0,
                shouldAsk,
                advisor is null ? null : advice is not null,
                advisorMs,
                advice?.Tools.ToArray(),
                advice?.Intent,
                advice?.Confidence,
                adviceError));

            var mark = missing.Length == 0 && forbidden.Length == 0 ? "PASS" : "MISS";
            Console.WriteLine(
                $"{testCase.Id,-4} {mark}  rule={(ruleMissing.Length == 0 && ruleForbidden.Length == 0 ? "hit " : "miss")} "
                + $"src={final.Source,-11} max={maxTools,-2} "
                + (shouldAsk ? $"advisor={(advice is null ? "fail" : "ok")} {advisorMs,4}ms " : string.Empty)
                + (missing.Length > 0 ? $"missing={string.Join(',', missing)}" : string.Empty)
                + (forbidden.Length > 0 ? $" forbidden={string.Join(',', forbidden)}" : string.Empty));
        }

        var ruleHits = results.Count(item => item.RuleHit);
        var hits = results.Count(item => item.Hit);
        var finalRate = results.Count == 0 ? 0 : (double)hits / results.Count;
        var ruleRate = results.Count == 0 ? 0 : (double)ruleHits / results.Count;
        var summary = new EvalSummary(
            options.Mode,
            options.ProviderId,
            options.ModelId,
            DateTimeOffset.Now,
            results.Count,
            ruleHits,
            ruleRate,
            hits,
            finalRate,
            results.Count(item => item.AdvisorAsked),
            results.Count(item => item.AdvisorAsked && item.AdvisorAnswered == true),
            Math.Round(results.Where(item => item.AdvisorAsked).Select(item => (double)item.AdvisorMs)
                .DefaultIfEmpty(0).Average(), 1),
            results);

        Console.WriteLine(
            $"\n路由评测：规则 {ruleHits}/{results.Count} ({ruleRate:P1})，"
            + $"最终 {hits}/{results.Count} ({finalRate:P1})，"
            + $"阈值 {options.MinHitRate:P0}");

        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            var full = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(summary, JsonOptions));
            Console.WriteLine($"结果：{full}");
        }

        return finalRate + 1e-9 >= options.MinHitRate ? 0 : 2;
    }

    /// <summary>
    /// 真实工具描述符：只用工具包的 Descriptor，不实例化工具实现，
    /// 因此评测不会读文件、不启动进程、不联网。
    /// </summary>
    private static AgentToolRegistry CreateRegistry() =>
        new(
        [
            new TodoToolPack(),
            new SearchToolPack(new EmptySearchService()),
            new SkillToolPack(),
            new FileToolPack(),
            new EditToolPack(),
            new MemoryToolPack(),
            new PdfToolPack(),
            new PowerShellToolPack(new NoopRunner()),
            new CommandToolPack(new NoopRunner(), new SessionGrantStore())
        ]);

    private static RoutingCapability[] BuildCapabilities(
        AgentToolRegistry registry,
        IReadOnlySet<string> packs) =>
        registry.Descriptors
            .Where(descriptor => packs.Contains(descriptor.Id))
            .SelectMany(descriptor => descriptor.Functions.Select(function => new RoutingCapability(
                function.Id,
                descriptor.Id,
                descriptor.Name,
                descriptor.Description)))
            .ToArray();

    private static (ModelProvider Provider, string ModelId, IRoutingAdvisor Advisor) LoadLiveRouter(
        CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new ArgumentException("--mode live 需要 --provider 与 --model。");
        }

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo", "DesktopAgent", "settings.json");
        var settings = LoadSettings(settingsPath);
        var provider = settings.Providers.FirstOrDefault(item => item.Id == options.ProviderId)
            ?? throw new InvalidOperationException($"找不到 provider：{options.ProviderId}");
        return (provider, options.ModelId, new SmallModelRoutingAdvisor(new OpenAiRoutingChatTransport()));
    }

    private static EvalCase[] LoadCases(string path)
    {
        var full = Path.GetFullPath(path);
        return JsonSerializer.Deserialize<EvalCase[]>(File.ReadAllText(full), JsonOptions)
            ?? throw new InvalidOperationException($"用例解析失败：{full}");
    }

    private static AppSettings LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到设置文件：{path}");
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("设置文件解析失败");
        var entropy = Encoding.UTF8.GetBytes("SDemo.DesktopAgent.ApiKey.v1");

        foreach (var provider in settings.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKeyProtected))
            {
                continue;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(provider.ApiKeyProtected),
                    entropy,
                    DataProtectionScope.CurrentUser);
                provider.ApiKey = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Console.Error.WriteLine($"provider {provider.Id} 的 Key 无法解密。");
            }
        }

        return settings;
    }

    private sealed record EvalCase(
        string Id,
        string Prompt,
        string[] Packs,
        string Mode,
        int Window,
        string[] ExpectedTools,
        string[] ForbiddenTools,
        string? Note,
        string[]? History = null);

    private sealed record CaseResult(
        string Id,
        string Prompt,
        string? Note,
        string Mode,
        int Window,
        int MaxTools,
        string[] Packs,
        string[] ExpectedTools,
        string[] ExposedTools,
        string[] Missing,
        string[] Unexpected,
        string[] ForbiddenExposed,
        string Source,
        string Reason,
        string RuleSource,
        string[] RuleExposedTools,
        bool RuleHit,
        bool Hit,
        bool AdvisorAsked,
        bool? AdvisorAnswered,
        long AdvisorMs,
        IReadOnlyList<string>? AdviceTools,
        string? AdviceIntent,
        double? AdviceConfidence,
        string? AdviceError);

    private sealed record EvalSummary(
        string Mode,
        string? ProviderId,
        string? ModelId,
        DateTimeOffset StartedAt,
        int Total,
        int RuleHits,
        double RuleHitRate,
        int Hits,
        double HitRate,
        int AdvisorCalls,
        int AdvisorSuccesses,
        double AdvisorAverageMs,
        IReadOnlyList<CaseResult> Results);

    private sealed class EmptySearchService : ISearchService
    {
        public Task<string> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class NoopRunner : IDirectProcessRunner
    {
        public Task<DirectRunResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            int maxOutputChars,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new DirectRunResult(0, string.Empty, string.Empty, false, false, 0, false, false));
    }

    private sealed class CliOptions
    {
        public string CasesPath { get; private set; } = Path.Combine("eval", "routing", "cases.json");
        public string Mode { get; private set; } = "rules";
        public string? ProviderId { get; private set; }
        public string? ModelId { get; private set; }
        public string? OutputPath { get; private set; }
        public double MinHitRate { get; private set; } = 0.9;
        public int TimeoutSeconds { get; private set; } = 10;
        public bool AlwaysAdvise { get; private set; }
        public HashSet<string> Only { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();

            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                string Next() => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"缺少 {key} 的值");

                switch (key)
                {
                    case "--cases": options.CasesPath = Next(); break;
                    case "--mode": options.Mode = Next().ToLowerInvariant(); break;
                    case "--provider": options.ProviderId = Next(); break;
                    case "--model": options.ModelId = Next(); break;
                    case "--out": options.OutputPath = Next(); break;
                    case "--min-hit-rate": options.MinHitRate = double.Parse(Next()); break;
                    case "--timeout": options.TimeoutSeconds = Math.Clamp(int.Parse(Next()), 1, 60); break;
                    case "--always-advise": options.AlwaysAdvise = true; break;
                    case "--only": options.Only.UnionWith(Next().Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                    default: throw new ArgumentException($"未知参数：{key}");
                }
            }

            if (options.Mode is not ("rules" or "live"))
            {
                throw new ArgumentException("--mode 只能是 rules 或 live。");
            }

            return options;
        }
    }
}
