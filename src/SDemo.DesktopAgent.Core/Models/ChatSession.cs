namespace SDemo.DesktopAgent.Models;

/// <summary>
/// 一个聊天室（会话）。空聊天室与有记录的聊天室使用同一份模型，
/// 界面由 ChatRoom 组件根据 <see cref="Messages"/> 是否为空切换形态。
/// </summary>
public sealed class ChatSession
{
    public required string Id { get; init; }

    public required string Title { get; set; }

    /// <summary>列表里显示的次要信息，例如时间。</summary>
    public string? Meta { get; set; }

    /// <summary>所属工作区；为空时落在默认工作区。</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>是否置顶（置顶后排在最前，取消置顶按钮常亮）。</summary>
    public bool IsPinned { get; set; }

    /// <summary>最后更新时间，列表排序用（置顶优先，其次按它倒序）。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>是否正在生成回复（运行期状态，不落库；左侧列表用于显示“推理中”）。</summary>
    public bool IsGenerating { get; set; }

    /// <summary>这个会话是从哪个插件入口创建的（<c>&lt;pluginId&gt;/&lt;mountId&gt;</c>）；为空表示普通会话。</summary>
    public string? OriginMountId { get; set; }

    /// <summary>输入框当前选中的插件入口；随会话持久，决定这一轮走插件还是普通对话。</summary>
    public string? ActiveMountId { get; set; }

    /// <summary>最后一次<b>实际运行</b>的插件 id；普通推理开始时清空。侧栏会话图标用它。</summary>
    public string? LastPluginId { get; set; }

    /// <summary>
    /// 会话级插件记忆：键为 <c>&lt;pluginId&gt;/&lt;mountId&gt;/&lt;key&gt;</c>，值为参数值；
    /// <c>ui.</c> 前缀保留给宿主（例如 <c>ui.mode</c>）。
    /// </summary>
    public Dictionary<string, string> PluginMemory { get; init; } = new(StringComparer.Ordinal);

    public List<ChatMessage> Messages { get; init; } = [];

    /// <summary>
    /// 滚动摘要正文：覆盖序号小于 <see cref="SummaryThroughOrder"/> 的消息。
    /// 原文仍完整保留在 <see cref="Messages"/> 与数据库里，摘要只影响「这次发什么」。
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>摘要覆盖到哪条消息的序号（不含）；0 表示还没有摘要。单调不减。</summary>
    public int SummaryThroughOrder { get; set; }

    /// <summary>摘要最后一次更新时间。</summary>
    public DateTime? SummaryUpdatedAt { get; set; }

    /// <summary>摘要正文的 token 估算（已按 SummaryMaxTokens 截断）。</summary>
    public int SummaryTokens { get; set; }

    /// <summary>摘要是否由确定性兜底生成（UI 上标注「自动摘要」）。</summary>
    public bool SummaryIsFallback { get; set; }

    /// <summary>是否有可用的滚动摘要。</summary>
    public bool HasSummary =>
        SummaryThroughOrder > 0 && !string.IsNullOrWhiteSpace(Summary);

    public bool IsEmpty => Messages.Count == 0;

    /// <summary>新建的空聊天室。首次发送消息后才写入数据库 / 历史列表。</summary>
    public static ChatSession CreateEmpty(string? workspaceId = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = "新聊天室",
        Meta = "刚刚",
        WorkspaceId = workspaceId,
        UpdatedAt = DateTime.Now,
        Messages = []
    };
}
