using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 小模型路由（M4，成本档 1）的输入：只给「当前可用能力」和本轮输入，
/// 不携带工具实现、文件内容或密钥，因此可以安全地发给任意 OpenAI 兼容端点。
/// </summary>
/// <param name="Provider">路由模型 provider（由宿主解析，通常是摘要模型或小模型）。</param>
/// <param name="ModelId">路由模型 id。</param>
/// <param name="UserInput">用户本轮原句。</param>
/// <param name="RecentToolIds">最近几轮实际调用过的工具名。</param>
/// <param name="Mode">本轮模式；Compact 不进入这一档。</param>
/// <param name="MaxExposedTools">本轮最多暴露的工具数（含元工具）。</param>
/// <param name="Capabilities">「平台支持 ∩ 用户启用」的工具清单。</param>
/// <param name="Skills">可选技能名，供路由判断是否需要 load_skill。</param>
public sealed record RoutingAdviceRequest(
    ModelProvider Provider,
    string ModelId,
    string UserInput,
    IReadOnlyList<string> RecentToolIds,
    AgentMode Mode,
    int MaxExposedTools,
    IReadOnlyList<RoutingCapability> Capabilities,
    IReadOnlyList<string> Skills);

/// <summary>路由提示里的一条能力：只包含模型选择所需的元数据。</summary>
public sealed record RoutingCapability(
    string Id,
    string PackId,
    string PackName,
    string Description);

/// <summary>
/// 小模型给出的路由建议。工具名必须落在 <see cref="RoutingAdviceRequest.Capabilities"/> 内；
/// 宿主会再次过滤，模型不能凭这个结果绕过用户开关。
/// </summary>
/// <param name="Tools">建议暴露的工具名，按优先级排列。</param>
/// <param name="Intent">模型对本轮意图的短标签（可选，仅用于展示 / 诊断）。</param>
/// <param name="Reason">一句话理由（可选，已截断）。</param>
/// <param name="Confidence">0–1 的置信度；低于阈值时宿主仍可使用，但会在理由里标注。</param>
public sealed record RoutingAdvice(
    IReadOnlyList<string> Tools,
    string? Intent = null,
    string? Reason = null,
    double Confidence = 1.0);

/// <summary>
/// 小模型路由接口（M4）。实现必须「不抛异常」（取消除外）：超时、网络错误、格式不符都返回 null，
/// 由 <see cref="Services.CapabilityRouter"/> 的规则结果兜底，绝不阻塞对话。
/// </summary>
public interface IRoutingAdvisor
{
    Task<RoutingAdvice?> AdviseAsync(
        RoutingAdviceRequest request,
        CancellationToken cancellationToken = default);
}
