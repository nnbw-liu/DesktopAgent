using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatMessageRole = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.P2Golden;

/// <summary>
/// P2 真机黄金集：120 轮会话（前 3 轮埋 3 条长期约束 + 项目事实），
/// 用真实模型做增量摘要，压缩后连续问 10 个只有早期历史才知道的问题。
/// 只读用户设置里的 provider / model / key，不写回设置。
/// 运行：dotnet run --project tools/p2-golden -c Release
/// 可用环境变量覆盖：P2_PROVIDER / P2_MODEL。
/// </summary>
internal static class Program
{
    private static readonly HeuristicTokenCounter Counter = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly int TotalTurns =
        int.TryParse(Environment.GetEnvironmentVariable("P2_TURNS"), out var turns) && turns > 20 ? turns : 120;

    private static readonly string Profile =
        Environment.GetEnvironmentVariable("P2_PROFILE") ?? "8K";

    public static async Task<int> Main(string[] args)
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent",
            "settings.json");

        if (!File.Exists(settingsPath))
        {
            Console.Error.WriteLine($"找不到设置文件：{settingsPath}");
            return 2;
        }

        var settings = LoadSettings(settingsPath);
        var providerId = Environment.GetEnvironmentVariable("P2_PROVIDER") ?? "火山";
        var provider = settings.Providers.FirstOrDefault(item => item.Id == providerId)
            ?? throw new InvalidOperationException($"设置里没有 provider：{providerId}");
        var modelId = Environment.GetEnvironmentVariable("P2_MODEL")
            ?? provider.Models.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"provider {providerId} 没有模型");

        // P2_PROFILE 档（默认 8K）模拟目标窗口；关掉状态包，让黄金集只测摘要这一条链路。
        settings.Context.KeepRecentTurns = 6;
        settings.Context.SmallModelKeepRecentTurns = 6;
        settings.Context.AutoCompact = true;
        settings.Context.StatePackEnabled = false;
        settings.Context.SummaryMaxTokens = 800;
        settings.Context.SummaryInputMaxTokens = 6000;
        settings.Context.SummaryTimeoutSeconds = 60;
        settings.Context.SummaryProviderId = provider.Id;
        settings.Context.SummaryModelId = modelId;
        settings.Context.MidTurnTrim = true;

        var scoped = new ModelProvider
        {
            Id = provider.Id,
            DisplayName = provider.DisplayName,
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            Models = [new ModelEntry { Id = modelId, Profile = ModelProfilePresets.FromPreset(Profile) }]
        };

        var settingsService = new InMemorySettings(settings);
        var builder = new ContextBuilder(Counter, settingsService);
        var forcedFallback = Environment.GetEnvironmentVariable("P2_FORCE_FALLBACK") == "1";
        IContextSummarizer summarizer = forcedFallback
            ? new ExtractiveContextSummarizer(Counter)
            : new LoggingSummarizer(new LlmContextSummarizer(new OpenAiSummaryChatTransport(), Counter));
        var compactor = new ContextCompactor(Counter, summarizer, new ExtractiveContextSummarizer(Counter));
        var chat = new GoldenChat(scoped, modelId);
        var runner = new AgentSessionRunner(chat, builder, settingsService, compactor: compactor);

        var session = BuildSession();
        var telemetry = new ContextRunTelemetry();
        var sw = Stopwatch.StartNew();

        var firstReply = new StringBuilder();
        await foreach (var delta in runner.StreamAsync(
            new AgentRunRequest(
                session.Messages.ToList(),
                scoped,
                modelId,
                new HashSet<string>(StringComparer.Ordinal),
                SessionId: session.Id,
                Session: session),
            telemetry))
        {
            firstReply.Append(delta.Text);
        }

        sw.Stop();

        var compaction = new CompactionMeasurement
        {
            Ok = session.SummaryThroughOrder > 0,
            ThroughOrder = session.SummaryThroughOrder,
            CompactedMessages = session.SummaryThroughOrder,
            SummaryTokens = session.SummaryTokens,
            Fallback = session.SummaryIsFallback,
            Notice = telemetry.ContextNotice,
            Estimated = telemetry.Report?.Estimated ?? 0,
            Total = telemetry.Report?.Total ?? 0,
            ContextWindow = telemetry.Report?.ContextWindow ?? 0,
            SummaryUsed = telemetry.Report?.SummaryUsed ?? false,
            ElapsedMs = sw.ElapsedMilliseconds,
            FirstReply = Truncate(firstReply.ToString(), 200),
            Summary = session.Summary ?? string.Empty
        };

        var questions = Questions();
        var results = new List<QuestionResult>();

        foreach (var question in questions)
        {
            var messages = session.Messages.Select(Clone).ToList();
            messages.Add(new ChatMessageModel(ChatMessageRole.User, question.Prompt));

            ContextPlan plan;

            try
            {
                plan = builder.Build(new ContextBuildRequest(
                    scoped,
                    modelId,
                    messages,
                    SummaryText: session.Summary,
                    SummaryThroughOrder: session.SummaryThroughOrder,
                    SummaryFallback: session.SummaryIsFallback));
            }
            catch (ContextBudgetException ex)
            {
                results.Add(new QuestionResult
                {
                    Id = question.Id,
                    Prompt = question.Prompt,
                    Answer = string.Empty,
                    Ok = false,
                    Error = ex.Message
                });
                continue;
            }

            try
            {
                var (answer, input, output) = await chat.AskAsync(plan, maxOutputTokens: 220);
                var ok = Grade(question, answer);

                results.Add(new QuestionResult
                {
                    Id = question.Id,
                    Prompt = question.Prompt,
                    Answer = answer,
                    Ok = ok,
                    InputTokens = input,
                    OutputTokens = output,
                    ContextEstimated = plan.Report.Estimated,
                    ContextTotal = plan.Report.Total,
                    SummaryTokens = plan.Report.Segments.Summary
                });
            }
            catch (Exception ex)
            {
                results.Add(new QuestionResult
                {
                    Id = question.Id,
                    Prompt = question.Prompt,
                    Answer = string.Empty,
                    Ok = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        var payload = new GoldenPayload
        {
            Provider = provider.Id,
            Model = modelId,
            Profile = Profile,
            Summarizer = forcedFallback ? "extractive" : "model",
            Turns = TotalTurns,
            Compaction = compaction,
            Correct = results.Count(item => item.Ok),
            Total = results.Count,
            Questions = results
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });

        var outputPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.json"));
        await File.WriteAllTextAsync(outputPath, json);

        Console.WriteLine(json);
        Console.WriteLine();
        Console.WriteLine($"正确 {payload.Correct}/{payload.Total}；压缩 {compaction.CompactedMessages} 条，"
            + $"摘要 {compaction.SummaryTokens} token，兜底={compaction.Fallback}。");

        return payload.Correct >= 8 && compaction.Ok ? 0 : 1;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static bool Grade(QuestionSpec question, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || answer.Contains("不知道", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var group in question.Keywords)
        {
            if (!group.Any(keyword => answer.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static List<ChatMessageModel> BuildSessionMessages()
    {
        var messages = new List<ChatMessageModel>
        {
            new(ChatMessageRole.User, "项目代号 Aurora，核心模块叫 LedgerSync，最终目标是把同步延迟降到 200ms。请记住。"),
            new(ChatMessageRole.Assistant, "已记录：项目 Aurora / 模块 LedgerSync / 目标 200ms。"),
            new(ChatMessageRole.User, "长期约束 1：新文件只能放在 src/ 下，不能放在仓库根目录。"),
            new(ChatMessageRole.Assistant, "已记录约束 1：新文件放 src/。"),
            new(ChatMessageRole.User, "长期约束 2：数据库 schema 不允许修改，只能新增只读视图。"),
            new(ChatMessageRole.Assistant, "已记录约束 2：schema 不可改，只读视图。"),
            new(ChatMessageRole.User, "长期约束 3：所有回答用中文，并且先给结论再解释。"),
            new(ChatMessageRole.Assistant, "已记录约束 3：中文 + 先结论。")
        };

        var filler = new[]
        {
            "继续重构 LedgerSync 的批处理管线，记录一个无关紧要的中间步骤。",
            "检查上一轮实现的测试覆盖，补充一个边界用例。",
            "整理模块的日志与错误码，保持既有约定不变。",
            "复核同步延迟的采样方式，暂不调整目标值。",
            "更新模块内的注释与命名，不改变对外行为。"
        };

        var index = 4;

        while (messages.Count / 2 < TotalTurns)
        {
            var text = filler[index % filler.Length];
            messages.Add(new ChatMessageModel(
                ChatMessageRole.User,
                $"第 {index} 轮：{text}（这是用于压测历史长度的填充内容，编号 {index:000}。）"));
            messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                $"第 {index} 轮已处理：{text}编号 {index:000} 的步骤没有引入新的长期约束，沿用前文约定。"));
            index++;
        }

        return messages;
    }

    private static ChatSession BuildSession() => new()
    {
        Id = "p2-golden",
        Title = "P2 golden",
        Messages = BuildSessionMessages()
    };

    private static ChatMessageModel Clone(ChatMessageModel message) => new(message.Role, message.Content)
    {
        Reasoning = message.Reasoning,
        CreatedAt = message.CreatedAt
    };

    private static List<QuestionSpec> Questions() =>
    [
        new(1, "项目代号是什么？只回答代号。", [["Aurora"]]),
        new(2, "核心模块叫什么名字？", [["LedgerSync"]]),
        new(3, "最终目标里的延迟指标是多少？", [["200"], ["延迟"]]),
        new(4, "新建文件应该放到哪个目录？", [["src"]]),
        new(5, "数据库 schema 允许修改吗？", [["不允许", "不可以", "不能", "不可修改", "不能修改"]]),
        new(6, "请复述三条长期约束（文件位置 / 数据库 / 回答语言）。", [["src"], ["schema"], ["中文"]]),
        new(7, "回答必须使用什么语言？", [["中文"]]),
        new(8, "每次回答要先给出什么？", [["结论"]]),
        new(9, "新文件可以放在仓库根目录吗？", [["不可以", "不能", "不允许", "不可"]]),
        new(10, "数据库只能通过什么方式扩展？", [["视图"]])
    ];

    private static AppSettings LoadSettings(string path)
    {
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
                Console.Error.WriteLine($"provider {provider.Id} 的 Key 无法解密，将按无 Key 处理。");
            }
        }

        return settings;
    }

    /// <summary>诊断：把每次摘要调用的结果打到 stderr，避免压缩器静默兜底。</summary>
    private sealed class LoggingSummarizer(IContextSummarizer inner) : IContextSummarizer
    {
        public async Task<SummaryResult> SummarizeAsync(
            SummaryRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.SummarizeAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.UsedModel)
            {
                Console.Error.WriteLine(
                    $"[summary] ok batchStart={request.ChunkStartOrder} tokens={result.Tokens}");
            }
            else
            {
                Console.Error.WriteLine(
                    $"[summary] FAIL batchStart={request.ChunkStartOrder} error={result.Error}");
            }

            return result;
        }
    }

    private sealed class InMemorySettings(AppSettings current) : ISettingsService    {
        public AppSettings Current { get; } = current;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void Save()
        {
        }
    }

    /// <summary>真实回答：系统提示词 + builder 组装的消息，非流式拿 usage。</summary>
    private sealed class GoldenChat(ModelProvider provider, string modelId) : IAgentChatService
    {
        public (string Answer, int Input, int Output) Last { get; private set; }

        public async IAsyncEnumerable<AgentDelta> RunAsync(
            AgentRunPlan plan,
            ContextRunTelemetry? telemetry = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var (answer, input, output) = await AskAsync(plan.Context, maxOutputTokens: 256, cancellationToken)
                .ConfigureAwait(false);

            Last = (answer, input, output);

            yield return new AgentDelta(answer, string.Empty);
        }

        public async Task<(string Answer, int Input, int Output)> AskAsync(
            ContextPlan plan,
            int maxOutputTokens,
            CancellationToken cancellationToken = default)
        {
            var credential = new ApiKeyCredential(
                string.IsNullOrWhiteSpace(provider.ApiKey) ? "not-required" : provider.ApiKey);

            var client = new OpenAIClient(credential, new OpenAIClientOptions
            {
                Endpoint = new Uri(provider.BaseUrl)
            }).GetChatClient(modelId).AsIChatClient();

            var messages = new List<AIChatMessage>
            {
                new(ChatRole.System, plan.Instructions)
            };
            messages.AddRange(plan.Messages);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(90));

            var response = await client.GetResponseAsync(
                messages,
                new ChatOptions { Temperature = 0f, MaxOutputTokens = maxOutputTokens },
                cts.Token).ConfigureAwait(false);

            return (
                response.Text?.Trim() ?? string.Empty,
                (int)(response.Usage?.InputTokenCount ?? 0),
                (int)(response.Usage?.OutputTokenCount ?? 0));
        }
    }

    private sealed record QuestionSpec(int Id, string Prompt, string[][] Keywords);

    private sealed class GoldenPayload
    {
        public required string Provider { get; init; }

        public required string Model { get; init; }

        public required string Profile { get; init; }

        public required string Summarizer { get; init; }

        public int Turns { get; init; }

        public required CompactionMeasurement Compaction { get; init; }

        public int Correct { get; init; }

        public int Total { get; init; }

        public required List<QuestionResult> Questions { get; init; }
    }

    private sealed class CompactionMeasurement
    {
        public bool Ok { get; init; }

        public int ThroughOrder { get; init; }

        public int CompactedMessages { get; init; }

        public int SummaryTokens { get; init; }

        public bool Fallback { get; init; }

        public string? Notice { get; init; }

        public int Estimated { get; init; }

        public int Total { get; init; }

        public int ContextWindow { get; init; }

        public bool SummaryUsed { get; init; }

        public long ElapsedMs { get; init; }

        public string? FirstReply { get; init; }

        public string? Summary { get; init; }
    }

    private sealed class QuestionResult
    {
        public int Id { get; init; }

        public required string Prompt { get; init; }

        public required string Answer { get; init; }

        public bool Ok { get; init; }

        public string? Error { get; init; }

        public int InputTokens { get; init; }

        public int OutputTokens { get; init; }

        public int ContextEstimated { get; init; }

        public int ContextTotal { get; init; }

        public int SummaryTokens { get; init; }
    }
}
