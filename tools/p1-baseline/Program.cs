using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using ChatAttachmentModel = SDemo.DesktopAgent.Models.ChatAttachment;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;
using ChatMessageRole = SDemo.DesktopAgent.Models.ChatMessageRole;

namespace SDemo.P1Baseline;

/// <summary>
/// P1 真机基线：用真实的 ContextBuilder + 中途裁剪装饰器 + MAF 发请求，
/// 记录「估算 token vs 服务端 usage」与 8K 预算 + 200 轮 + 50KB 附件的真实结果。
/// 只读用户设置里的 provider / model / key，不写回设置。
/// 运行：dotnet run --project tools/p1-baseline -c Release
/// </summary>
internal static class Program
{
    private static readonly HeuristicTokenCounter Counter = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] LocalModels =
    [
        "qwen3.5-4b@q6_k",
        "qwen3.5-9b@q6_k",
        "qwen/qwen3.6-35b-a3b"
    ];

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
        var settingsService = new InMemorySettings(settings);
        var builder = new ContextBuilder(Counter, settingsService);

        var local = settings.Providers.FirstOrDefault(provider => provider.Id == "local_lm")
            ?? throw new InvalidOperationException("设置里没有 local_lm provider");
        var cloud = settings.Providers.FirstOrDefault(provider => provider.Id == "火山")
            ?? throw new InvalidOperationException("设置里没有 火山 provider");

        var attachment = CreateAttachment(50 * 1024);
        var rows = new List<Measurement>();

        // 1. 短对话：测启发式估算误差（不挂校准，measure the raw heuristic）。
        foreach (var model in LocalModels)
        {
            rows.Add(await MeasureAsync(
                builder, local, settings, model, "short-10轮", BuildShortHistory(10), attachment: null, profile: "8K"));
        }

        rows.Add(await MeasureAsync(
            builder, cloud, settings, "deepseek-v4.1-flash", "short-10轮", BuildShortHistory(10), attachment: null, profile: "8K"));

        // 2. 8K + 200 轮 + 50KB 附件：必须成功（不 400），且请求大小受预算约束。
        foreach (var model in LocalModels)
        {
            rows.Add(await MeasureAsync(
                builder, local, settings, model, "200轮+50KB附件", BuildLongHistory(200), attachment, profile: "8K"));
        }

        rows.Add(await MeasureAsync(
            builder, cloud, settings, "deepseek-v4.1-flash", "200轮+50KB附件", BuildLongHistory(200), attachment, profile: "8K"));

        // 3. 校准前后：同一模型连续 4 次，观察误差收敛（每次一个新 store，模拟真实累计）。
        rows.AddRange(await MeasureCalibrationAsync(builder, local, settings, "qwen3.5-4b@q6_k"));

        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var output = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.json");
        await File.WriteAllTextAsync(Path.GetFullPath(output), json);

        Console.WriteLine(json);
        return rows.All(row => row.Ok) ? 0 : 1;
    }

    private static async Task<List<Measurement>> MeasureCalibrationAsync(
        ContextBuilder builder,
        ModelProvider provider,
        AppSettings settings,
        string modelId)
    {
        var rows = new List<Measurement>();
        var history = BuildShortHistory(6);
        var calibration = new TokenCalibrationStore(Path.Combine(Path.GetTempPath(), $"p1-baseline-{Guid.NewGuid():N}.json"));
        var calibratedBuilder = new ContextBuilder(Counter, new InMemorySettings(settings), calibration);
        var factorBefore = 1.0;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var measurement = await MeasureAsync(
                calibratedBuilder, provider, settings, modelId, $"校准第{attempt + 1}次", history, null, "8K",
                calibration: calibration);

            measurement.CalibrationFactor = factorBefore;
            rows.Add(measurement);
            factorBefore = calibration.GetFactor(provider.Id, modelId);
        }

        return rows;
    }

    private static async Task<Measurement> MeasureAsync(
        ContextBuilder builder,
        ModelProvider provider,
        AppSettings settings,
        string modelId,
        string scenario,
        List<ChatMessageModel> history,
        ChatAttachmentModel? attachment,
        string profile,
        TokenCalibrationStore? calibration = null)
    {
        // 用 8K 档模拟 8K 模型（本机/云端真实窗口更大，但请求大小由预算决定）。
        var models = provider.Models.Select(model => model.Id).ToList();
        if (!models.Contains(modelId))
        {
            models.Add(modelId);
        }

        var scopedProvider = new ModelProvider
        {
            Id = provider.Id,
            DisplayName = provider.DisplayName,
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            Models = models.Select(id => new ModelEntry
            {
                Id = id,
                Profile = string.Equals(id, modelId, StringComparison.Ordinal)
                    ? ModelProfilePresets.FromPreset(profile)
                    : null
            }).ToList()
        };

        // 附件挂在本轮最后一条 user 消息上。
        var messages = history.Select(Clone).ToList();
        if (attachment is not null)
        {
            var lastUser = messages[^1];
            messages[^1] = new ChatMessageModel(lastUser.Role, "请总结这个附件，然后只回复两个字：已读")
            {
                Attachments = [attachment]
            };
        }

        var request = new ContextBuildRequest(scopedProvider, modelId, messages);
        ContextPlan plan;

        try
        {
            plan = builder.Build(request);
        }
        catch (ContextBudgetException ex)
        {
            return new Measurement
            {
                Provider = provider.Id,
                Model = modelId,
                Scenario = scenario,
                Ok = false,
                Error = ex.Message,
                Estimated = ex.Report?.Estimated ?? 0,
                Total = ex.Report?.Total ?? 0,
                Turns = history.Count(message => message.Role == ChatMessageRole.User)
            };
        }

        var rawChars = history.Sum(message => (message.Content?.Length ?? 0));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var (inputTokens, outputTokens, reply, overflow, midTurn) = await SendAsync(plan, settings);
            sw.Stop();

            var errorPercent = inputTokens > 0
                ? (inputTokens - plan.Report.Estimated) * 100.0 / inputTokens
                : (double?)null;

            return new Measurement
            {
                Provider = provider.Id,
                Model = modelId,
                Scenario = scenario,
                Ok = true,
                Estimated = plan.Report.Estimated,
                ActualInput = inputTokens,
                ActualOutput = outputTokens,
                ErrorPercent = errorPercent is null ? null : Math.Round(errorPercent.Value, 2),
                Total = plan.Report.Total,
                Window = plan.Report.ContextWindow,
                Turns = plan.Report.KeptTurns,
                Omitted = plan.Report.OmittedMessages,
                AttachmentsStubbed = plan.Report.AttachmentsStubbed,
                RawContentChars = rawChars,
                ContextChars = plan.Messages.Sum(message => message.Text?.Length ?? 0),
                MidTurnTrimmed = midTurn,
                OverflowDetected = overflow,
                ElapsedMs = sw.ElapsedMilliseconds,
                Reply = reply
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var overflow = ContextOverflowDetector.TryDetect(ex, out var info);

            return new Measurement
            {
                Provider = provider.Id,
                Model = modelId,
                Scenario = scenario,
                Ok = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Estimated = plan.Report.Estimated,
                Total = plan.Report.Total,
                Turns = plan.Report.KeptTurns,
                Omitted = plan.Report.OmittedMessages,
                OverflowDetected = overflow,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// 与 AgentChatService 相同的请求管线：ContextBuilder 的产物 + 中途裁剪装饰器。
    /// 基线用非流式拿可靠 usage；工具循环（FICC）由单元测试与 UI 覆盖。
    /// </summary>
    private static async Task<(int Input, int Output, string Reply, bool Overflow, bool MidTurnTrimmed)> SendAsync(
        ContextPlan plan,
        AppSettings settings)
    {
        var provider = settings.Providers.Single(item => item.Id == plan.ProviderId);
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(provider.ApiKey) ? "not-required" : provider.ApiKey);

        var client = new OpenAIClient(credential, new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl)
        }).GetChatClient(plan.ModelId);

        var telemetry = new ContextRunTelemetry { Plan = plan, Report = plan.Report };
        var contextSettings = settings.Context;

        var guarded = new ContextBudgetChatClient(
            client.AsIChatClient(),
            new ContextBudgetChatOptions(
                Enabled: true,
                Budget: plan.Budget,
                Counter: Counter,
                CalibrationFactor: plan.Report.CalibrationFactor,
                KeepRecentTurns: contextSettings.KeepRecentTurns,
                ToolResultChars: contextSettings.MidTurnToolResultChars),
            telemetry);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, plan.Instructions)
        };
        messages.AddRange(plan.Messages);

        var overflow = false;
        ChatResponse? response = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            response = await guarded.GetResponseAsync(
                messages,
                new ChatOptions { MaxOutputTokens = 32 },
                cts.Token);
        }
        catch (Exception ex)
        {
            overflow = ContextOverflowDetector.TryDetect(ex, out _);
            if (!overflow)
            {
                throw;
            }
        }

        return (
            (int)(response?.Usage?.InputTokenCount ?? 0),
            (int)(response?.Usage?.OutputTokenCount ?? 0),
            response?.Text?.Trim() ?? string.Empty,
            overflow,
            telemetry.MidTurnTrimmedRequests > 0);
    }

    private static List<ChatMessageModel> BuildShortHistory(int turns)
    {
        var messages = new List<ChatMessageModel>();

        for (var index = 0; index < turns; index++)
        {
            messages.Add(new ChatMessageModel(
                ChatMessageRole.User,
                $"第 {index + 1} 轮：请用一句话解释什么是上下文预算，并给出一个例子。"));
            messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                $"第 {index + 1} 轮回答：上下文预算就是给一次请求能装多少 token 设上限，"
                + "超出的部分要么裁掉，要么压缩；例如 8K 窗口只留最近几轮，老消息折叠成摘要。"));
        }

        messages.Add(new ChatMessageModel(ChatMessageRole.User, "只回复两个字：已读"));
        return messages;
    }

    private static List<ChatMessageModel> BuildLongHistory(int turns)
    {
        var messages = new List<ChatMessageModel>();

        for (var index = 0; index < turns; index++)
        {
            messages.Add(new ChatMessageModel(
                ChatMessageRole.User,
                $"第 {index + 1} 轮：这是一段用于压测上下文预算的历史问题，"
                + "包含路径 D:/work/案件材料/第" + (index + 1) + "卷/说明.md，"
                + "请记住编号 " + (1000 + index) + " 以及要求：回答要短。"));
            messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                $"第 {index + 1} 轮回答：已记录编号 {1000 + index}；"
                + "材料路径已登记，后续如需细节我会用工具重新读取，而不是靠记忆复述。"));
        }

        messages.Add(new ChatMessageModel(ChatMessageRole.User, "只回复两个字：已读"));
        return messages;
    }

    private static ChatAttachmentModel CreateAttachment(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), $"p1-baseline-{Guid.NewGuid():N}.txt");
        var text = new StringBuilder(size);

        while (text.Length < size)
        {
            text.Append("附件正文用于验证 50KB 附件在 8K 预算下不会把请求顶爆。");
        }

        File.WriteAllText(path, text.ToString());
        return new ChatAttachmentModel
        {
            FileName = "50kb.txt",
            FilePath = path,
            Size = new FileInfo(path).Length
        };
    }

    private static ChatMessageModel Clone(ChatMessageModel message) => new(message.Role, message.Content)
    {
        Reasoning = message.Reasoning,
        Attachments = [.. message.Attachments],
        CreatedAt = message.CreatedAt
    };

    private static AppSettings LoadSettings(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("设置文件解析失败");

        // 与 DpapiSecureStore 相同的 entropy / scope；只在基线工具里用，不写回。
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

    private sealed class InMemorySettings(AppSettings current) : ISettingsService
    {
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

    private sealed class Measurement
    {
        public required string Provider { get; init; }

        public required string Model { get; init; }

        public required string Scenario { get; init; }

        public bool Ok { get; set; }

        public string? Error { get; set; }

        public int Estimated { get; set; }

        public int? ActualInput { get; set; }

        public int? ActualOutput { get; set; }

        public double? ErrorPercent { get; set; }

        public double? CalibrationFactor { get; set; }

        public int Total { get; set; }

        public int Window { get; set; }

        public int Turns { get; set; }

        public int Omitted { get; set; }

        public bool AttachmentsStubbed { get; set; }

        public int RawContentChars { get; set; }

        public int ContextChars { get; set; }

        public bool MidTurnTrimmed { get; set; }

        public bool OverflowDetected { get; set; }

        public long ElapsedMs { get; set; }

        public string? Reply { get; set; }
    }
}
