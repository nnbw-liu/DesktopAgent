using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 智能模型路由的工作方式。每一档都比上一档多一点成本、多一点判断力：
/// <list type="bullet">
/// <item><see cref="Off"/>：固定模型，和旧版本完全一样。</item>
/// <item><see cref="Rules"/>：本地规则判断任务类型，再按用户配置的映射选模型；零额外请求。</item>
/// <item><see cref="Hybrid"/>：规则拿不准时才问一次路由模型，模型只判断任务类型，模型仍由用户映射决定。</item>
/// <item><see cref="Model"/>：路由模型直接选模型（只能从宿主给的候选清单里选），适合想让模型总控的场景。</item>
/// </list>
/// </summary>
public enum ModelRoutingMode
{
    Off = 0,
    Rules = 1,
    Hybrid = 2,
    Model = 3
}

/// <summary>路由要识别的任务类型。与「用户设置里给每类任务指定模型」一一对应。</summary>
public enum ModelTaskKind
{
    /// <summary>寒暄 / 致谢 / 身份 / 明确不要工具的闲聊。</summary>
    Chat = 0,

    /// <summary>简短问答、解释、小范围只读查询。</summary>
    Simple = 1,

    /// <summary>改代码、跑构建 / 测试、看日志、单文件或少量文件的工程任务。</summary>
    Code = 2,

    /// <summary>架构 / 排查 / 重构 / 多文件改造 / 长上下文等值得上强模型的任务。</summary>
    Complex = 3
}

/// <summary>本轮模型是怎么定下来的，用于界面解释与评测归因。</summary>
public enum ModelRoutingSource
{
    /// <summary>用户在当前聊天室手选了固定模型，或路由关闭。</summary>
    Manual = 0,

    /// <summary>本地规则判定。</summary>
    Rules = 1,

    /// <summary>路由模型给出的判定。</summary>
    Advisor = 2,

    /// <summary>沿用同一会话上一轮的模型（减少切换、保住提示缓存）。</summary>
    Sticky = 3,

    /// <summary>配置失效 / 超时 / 无可用模型后的兜底。</summary>
    Fallback = 4
}

/// <summary>成本偏好：只作为路由模型的决策提示和候选排序依据，不改变用户显式映射。</summary>
public enum ModelCostPreference
{
    Economy = 0,
    Balanced = 1,
    Quality = 2
}

/// <summary>「某类任务用哪个模型」的一条映射。ProviderId + ModelId 必须成对出现。</summary>
public sealed class ModelRouteBinding
{
    public ModelTaskKind Task { get; set; }

    public string? ProviderId { get; set; }

    public string? ModelId { get; set; }

    /// <summary>两半都填了才算有效；只填一半时整条忽略，避免拿 A 家的模型去请求 B 家。</summary>
    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProviderId) && !string.IsNullOrWhiteSpace(ModelId);
}

/// <summary>
/// 智能模型路由设置（settings.json 的 <c>modelRouting</c> 节点）。
/// 默认关闭；旧设置文件没有该节点时按默认值处理，不做迁移。
/// </summary>
public sealed class ModelRoutingSettings
{
    /// <summary>路由模型输出上限：只需要一个任务类型、一个模型名和一句理由。</summary>
    public const int AdvisorMaxOutputTokens = 400;

    /// <summary>路由模型看到的候选模型上限，避免提示词膨胀。</summary>
    public const int MaxAdvisorCandidates = 24;

    /// <summary>工作方式；默认关闭，保持旧行为。</summary>
    public ModelRoutingMode Mode { get; set; } = ModelRoutingMode.Off;

    /// <summary>每类任务的模型映射；没有配置的类别回退到 <see cref="DefaultProviderId"/> / <see cref="DefaultModelId"/>。</summary>
    public List<ModelRouteBinding> Routes { get; set; } = [];

    /// <summary>兜底模型 provider id；与 <see cref="DefaultModelId"/> 成对，为空时用当前聊天室模型。</summary>
    public string? DefaultProviderId { get; set; }

    /// <summary>兜底模型 id；与 <see cref="DefaultProviderId"/> 成对。</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>路由模型 provider id；为空时复用摘要模型，再退到当前会话模型。</summary>
    public string? RouterProviderId { get; set; }

    /// <summary>路由模型 id；为空时复用摘要模型，再退到当前会话模型。</summary>
    public string? RouterModelId { get; set; }

    /// <summary>路由模型超时（秒）；超时直接回落规则，不阻塞对话。</summary>
    public int RouterTimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// 同一会话里任务类型不变时沿用上一轮的模型（默认开启）。
    /// 切模型会让服务端提示缓存失效，粘住能省一笔；任务类型变化时仍会重新路由。
    /// </summary>
    public bool StickySession { get; set; } = true;

    /// <summary>候选模型必须能装下本轮估算输入；装不下时跳过它。</summary>
    public bool RespectContextWindow { get; set; } = true;

    /// <summary>成本偏好，只影响路由模型的候选清单排序与提示，不影响用户映射。</summary>
    public ModelCostPreference CostPreference { get; set; } = ModelCostPreference.Balanced;

    /// <summary>
    /// 混合模式的「拿不准」阈值：规则置信度低于该值才调用路由模型。
    /// 只影响 <see cref="ModelRoutingMode.Hybrid"/>；规则模式从不调用，总控模式总是调用。
    /// </summary>
    public double AdvisorThreshold { get; set; } = 0.65;

    /// <summary>找到某类任务的显式映射；没有时返回 null。</summary>
    public ModelRouteBinding? Find(ModelTaskKind task) =>
        Routes.FirstOrDefault(route => route.Task == task && route.IsConfigured);

    /// <summary>写入一条映射；<paramref name="providerId"/> 或 <paramref name="modelId"/> 为空时删除该条。</summary>
    public void SetRoute(ModelTaskKind task, string? providerId, string? modelId)
    {
        Routes.RemoveAll(route => route.Task == task);

        if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId))
        {
            Routes.Add(new ModelRouteBinding
            {
                Task = task,
                ProviderId = providerId.Trim(),
                ModelId = modelId.Trim()
            });
        }
    }

    /// <summary>兜底映射是否成对配置。</summary>
    [JsonIgnore]
    public bool HasDefault =>
        !string.IsNullOrWhiteSpace(DefaultProviderId) && !string.IsNullOrWhiteSpace(DefaultModelId);

    /// <summary>复制一份并深拷贝映射列表，供按作用域合并时独立修改。</summary>
    public ModelRoutingSettings Clone() => new()
    {
        Mode = Mode,
        Routes = Routes.Select(route => new ModelRouteBinding
        {
            Task = route.Task,
            ProviderId = route.ProviderId,
            ModelId = route.ModelId
        }).ToList(),
        DefaultProviderId = DefaultProviderId,
        DefaultModelId = DefaultModelId,
        RouterProviderId = RouterProviderId,
        RouterModelId = RouterModelId,
        RouterTimeoutSeconds = RouterTimeoutSeconds,
        StickySession = StickySession,
        RespectContextWindow = RespectContextWindow,
        CostPreference = CostPreference,
        AdvisorThreshold = AdvisorThreshold
    };
}

/// <summary>路由结果的展示名（界面 / 日志共用）。</summary>
public static class ModelTaskKindNames
{
    public static string ToDisplayName(this ModelTaskKind task) => task switch
    {
        ModelTaskKind.Chat => "闲聊",
        ModelTaskKind.Simple => "简单",
        ModelTaskKind.Code => "代码",
        ModelTaskKind.Complex => "复杂",
        _ => task.ToString()
    };

    public static string ToDisplayName(this ModelRoutingSource source) => source switch
    {
        ModelRoutingSource.Manual => "手动",
        ModelRoutingSource.Rules => "规则",
        ModelRoutingSource.Advisor => "模型",
        ModelRoutingSource.Sticky => "沿用",
        ModelRoutingSource.Fallback => "兜底",
        _ => source.ToString()
    };
}
