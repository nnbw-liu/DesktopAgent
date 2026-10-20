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

    public List<ChatMessage> Messages { get; init; } = [];

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
