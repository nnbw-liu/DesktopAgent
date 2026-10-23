using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;

namespace SDemo.R1Validate;

/// <summary>R1 校验用的临时目录。</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdemo-r1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留一点临时文件不影响校验结论。
        }
    }
}

internal sealed class StubPaths(string root) : IAppPaths
{
    public string DataRoot => root;
    public string SettingsFile => System.IO.Path.Combine(root, "settings.json");
    public string ChatsRoot => System.IO.Path.Combine(root, "chats");
    public string UploadsRoot => System.IO.Path.Combine(root, "uploads");
    public string SkillsRoot => System.IO.Path.Combine(root, "skills");
    public string PluginsRoot => System.IO.Path.Combine(root, "plugins");
    public string DefaultWorkspaceRoot => System.IO.Path.Combine(root, "workspace");
    public string SandboxRoot => System.IO.Path.Combine(root, "sandbox");
}

internal sealed class StubSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public void Save() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>校验里不弹审批：任何请求都按“批准一次”处理。</summary>
internal sealed class AutoApprovals : IToolApprovalService
{
    public ToolApprovalRequest? Current => null;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolApprovalDecision.ApproveOnce);

    public void Resolve(ToolApprovalDecision decision)
    {
    }

    public void CancelAll()
    {
    }
}

internal sealed class NullAudit : IToolAuditLog
{
    public void Write(ToolAuditEntry entry)
    {
    }
}

/// <summary>
/// 生成 30 个测试工具：固定随机种子、三种参数形状、较长的中文描述，
/// 用于测量「30 工具」场景下的真实 schema token，而不是按名字粗估。
/// </summary>
internal sealed class GeneratedToolPack : IAgentToolPack
{
    public const string PackId = "r1-generated";
    public const int ToolCount = 30;

    private static readonly string[] Topics =
    [
        "符号索引", "依赖解析", "编码检测", "大文件分片", "并发写入", "路径归一化",
        "配置校验", "迁移预检", "日志聚合", "接口快照", "文本 diff", "资源清单"
    ];

    public GeneratedToolPack()
    {
        var random = new Random(20261023);
        var functions = new List<AITool>(ToolCount);
        var descriptors = new List<AgentToolFunctionDescriptor>(ToolCount);

        for (var index = 0; index < ToolCount; index++)
        {
            var name = $"r1_tool_{index + 1:00}_{Topics[index % Topics.Length]}";
            var topic = Topics[random.Next(Topics.Length)];
            var description =
                $"R1 校验用工具：读取并处理 {topic} 相关信息，返回结构化摘要；"
                + $"支持递归扫描与增量结果，编号 {index + 1:00}。"
                + "只读，不会修改工作区文件。";

            AITool tool = (index % 3) switch
            {
                0 => AIFunctionFactory.Create(
                    (string path, string query) => $"{name}: {path} {query}",
                    name,
                    description),
                1 => AIFunctionFactory.Create(
                    (string path, int limit, bool recursive) => $"{name}: {path} {limit} {recursive}",
                    name,
                    description),
                _ => AIFunctionFactory.Create(
                    (string pattern, string root, string mode) => $"{name}: {pattern} {root} {mode}",
                    name,
                    description)
            };

            functions.Add(tool);
            descriptors.Add(new AgentToolFunctionDescriptor(name, topic));
        }

        _functions = functions;
        Descriptor = new AgentToolPackDescriptor(
            PackId,
            "R1 校验工具",
            "30 个生成工具，只用于 schema 预算与路由验收。",
            "R1",
            AgentToolRisk.Low,
            Order: 90,
            DefaultEnabled: true,
            WindowsOnly: false,
            descriptors);
    }

    private readonly IReadOnlyList<AITool> _functions;

    public AgentToolPackDescriptor Descriptor { get; }

    public IReadOnlyList<AITool> Create(ToolContext context) => _functions;
}

/// <summary>直接调 OpenAI 兼容接口，验证「带工具 schema 的请求能发出」。</summary>
internal static class LiveToolRequest
{
    public static async Task<LiveRequestResult> SendAsync(
        AppSettings settings,
        string providerId,
        string modelId,
        IReadOnlyList<AITool> tools,
        CancellationToken cancellationToken)
    {
        var provider = settings.Providers.FirstOrDefault(item =>
            string.Equals(item.Id, providerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"找不到 provider：{providerId}");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = new[]
            {
                new { role = "user", content = "只回复 OK，不要调用任何工具。" }
            },
            ["tools"] = tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = JsonSerializer.Deserialize<JsonElement>(
                        (tool is AIFunction function ? function.JsonSchema : default).GetRawText())
                }
            }).ToArray(),
            ["max_tokens"] = 32,
            ["temperature"] = 0
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var endpoint = new Uri(provider.BaseUrl.TrimEnd('/') + "/chat/completions");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                provider.ApiKey);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            started.Stop();

            return new LiveRequestResult(
                Sent: true,
                Status: (int)response.StatusCode,
                ElapsedMs: started.ElapsedMilliseconds,
                ToolCount: tools.Count,
                BodyExcerpt: body.Length <= 400 ? body : body[..400],
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            started.Stop();
            return new LiveRequestResult(
                Sent: false,
                Status: 0,
                ElapsedMs: started.ElapsedMilliseconds,
                ToolCount: tools.Count,
                BodyExcerpt: string.Empty,
                Error: ex.Message);
        }
    }

    /// <summary>从 %APPDATA% 读取 settings.json 并解密 Key（与 routing-eval 同一口径）。</summary>
    public static AppSettings LoadSettings(string? path = null)
    {
        path ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent",
            "settings.json");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到设置文件：{path}");
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }) ?? throw new InvalidOperationException("settings.json 解析失败。");

        var entropy = Encoding.UTF8.GetBytes("SDemo.DesktopAgent.settings.v1");

        foreach (var provider in settings.Providers.Where(item => !string.IsNullOrWhiteSpace(item.ApiKeyProtected)))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(provider.ApiKeyProtected!),
                    entropy,
                    DataProtectionScope.CurrentUser);
                provider.ApiKey = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // 解不开就按没有 Key 处理：live 请求会失败，但校验结果会写清楚失败原因。
                provider.ApiKey = string.Empty;
            }
        }

        return settings;
    }
}

internal sealed record LiveRequestResult(
    bool Sent,
    int Status,
    long ElapsedMs,
    int ToolCount,
    string BodyExcerpt,
    string? Error);
