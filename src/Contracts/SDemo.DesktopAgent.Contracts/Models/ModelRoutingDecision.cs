namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 一轮请求最终使用的模型。路由只决定「用哪个模型」，不改变上下文预算、
/// 工具路由与会话历史；这些仍由原有组件按解析后的模型重新计算。
/// </summary>
/// <param name="Provider">最终 provider（与 <paramref name="ModelId"/> 成对）。</param>
/// <param name="ModelId">最终模型 id。</param>
/// <param name="Task">本轮识别出的任务类型。</param>
/// <param name="Source">决策来源，用于界面解释与评测归因。</param>
/// <param name="Reason">一句话理由（可展示，不超过约 80 字）。</param>
/// <param name="Confidence">规则 / 模型给出的置信度（0–1）。</param>
/// <param name="Complexity">规则估算的复杂度（0–100），仅用于解释与评测。</param>
public sealed record ModelRoutingDecision(
    ModelProvider Provider,
    string ModelId,
    ModelTaskKind Task,
    ModelRoutingSource Source,
    string Reason,
    double Confidence = 1.0,
    int Complexity = 50)
{
    /// <summary>本轮是否由路由（而不是手动固定模型）决定。</summary>
    public bool IsAuto => Source != ModelRoutingSource.Manual;

    /// <summary>界面上的短标签，例如「代码 · 规则」。</summary>
    public string Label => $"{Task.ToDisplayName()} · {Source.ToDisplayName()}";
}

/// <summary>
/// 一次模型路由的输入。只包含选模型需要的元数据：用户原句、最近文本、模型目录与设置；
/// 不包含文件内容、工具实现或密钥，因此可以安全地只把候选摘要交给路由模型。
/// </summary>
/// <param name="UserInput">用户本轮原句。</param>
/// <param name="Messages">当前会话消息（用于估算上下文长度）；路由只读取末尾少量文本。</param>
/// <param name="CurrentProvider">当前聊天室 / 兜底 provider。</param>
/// <param name="CurrentModelId">当前聊天室 / 兜底模型。</param>
/// <param name="Providers">全部已配置 provider。</param>
/// <param name="Settings">路由设置。</param>
/// <param name="IsAuto">当前作用域是否启用自动路由；false 时直接返回 <paramref name="CurrentModelId"/>。</param>
/// <param name="SessionId">会话 id，用于粘性路由与缓存。</param>
/// <param name="HasSelectedSkill">用户是否手选了技能（按任务处理，不判闲聊）。</param>
/// <param name="EnabledToolPacks">本轮启用的工具包 id；用于判断任务是否真的需要工具能力。</param>
public sealed record ModelRoutingRequest(
    string UserInput,
    IReadOnlyList<ChatMessage> Messages,
    ModelProvider CurrentProvider,
    string CurrentModelId,
    IReadOnlyList<ModelProvider> Providers,
    ModelRoutingSettings Settings,
    bool IsAuto,
    string SessionId = "",
    bool HasSelectedSkill = false,
    IReadOnlySet<string>? EnabledToolPacks = null,
    bool AllowAdvisor = true);

/// <summary>
/// 模型路由入口。实现必须「永不抛异常」（取消除外）：配置坏了、路由模型超时、
/// 候选全被过滤掉时都要返回一个可用的手动 / 兜底决策，不能阻塞对话。
/// </summary>
public interface IModelRouter
{
    Task<ModelRoutingDecision> RouteAsync(
        ModelRoutingRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>用户手选模型或改变路由设置后，清掉该会话的粘性决策。</summary>
    void ClearSession(string sessionId);
}

/// <summary>路由模型看到的一个候选：只有选模型需要的元数据，不含密钥。</summary>
/// <param name="ProviderId">provider id。</param>
/// <param name="ProviderName">provider 显示名。</param>
/// <param name="ModelId">模型 id。</param>
/// <param name="ContextWindow">上下文窗口（按配置 / 名称推断）。</param>
/// <param name="SupportsTools">是否支持工具调用。</param>
/// <param name="InputPricePerMillion">输入价格（每百万 token，可空）。</param>
/// <param name="OutputPricePerMillion">输出价格（每百万 token，可空）。</param>
/// <param name="RecommendedFor">用户为该模型配置的任务类型（空表示未绑定）。</param>
public sealed record ModelRoutingCandidate(
    string ProviderId,
    string ProviderName,
    string ModelId,
    int ContextWindow,
    bool SupportsTools,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    IReadOnlyList<ModelTaskKind> RecommendedFor);

/// <summary>交给路由模型的请求。</summary>
/// <param name="RouterProvider">承载路由调用的 provider（可以是小模型 / 摘要模型）。</param>
/// <param name="RouterModelId">路由模型 id。</param>
/// <param name="UserInput">用户本轮原句。</param>
/// <param name="RecentContext">最近对话的截断文本，只用于判断上下文。</param>
/// <param name="RuleTask">规则初判，供模型参考。</param>
/// <param name="Complexity">规则估算的复杂度。</param>
/// <param name="Candidates">宿主允许选择的候选模型。</param>
/// <param name="CostPreference">成本偏好。</param>
public sealed record ModelRoutingAdviceRequest(
    ModelProvider RouterProvider,
    string RouterModelId,
    string UserInput,
    string RecentContext,
    ModelTaskKind RuleTask,
    int Complexity,
    IReadOnlyList<ModelRoutingCandidate> Candidates,
    ModelCostPreference CostPreference);

/// <summary>路由模型返回的建议。模型名 / provider 由宿主再次校验，不能越出候选清单。</summary>
public sealed record ModelRoutingAdvice(
    ModelTaskKind Task,
    string? ProviderId = null,
    string? ModelId = null,
    string? Reason = null,
    double Confidence = 1.0);

/// <summary>
/// 路由模型的传输无关接口。实现必须把超时 / 网络 / JSON 非法都收敛成 null，
/// 由 <see cref="IModelRouter"/> 回落规则，绝不影响本轮对话。
/// </summary>
public interface IModelRoutingAdvisor
{
    Task<ModelRoutingAdvice?> AdviseAsync(
        ModelRoutingAdviceRequest request,
        CancellationToken cancellationToken = default);
}
