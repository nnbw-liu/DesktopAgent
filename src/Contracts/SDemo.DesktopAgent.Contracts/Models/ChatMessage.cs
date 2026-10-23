namespace SDemo.DesktopAgent.Models;

/// <summary>消息角色。</summary>
public enum ChatMessageRole
{
    User,
    Assistant
}
/// <summary>一条聊天消息。助手消息在流式生成过程中会不断追加文本与思维链。</summary>
public sealed class ChatMessage(ChatMessageRole role, string content)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public ChatMessageRole Role { get; } = role;

    public string Content { get; set; } = content;

    /// <summary>思维链（reasoning_content），为空表示模型没有返回。</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>随这条消息一起发送的附件。</summary>
    public List<ChatAttachment> Attachments { get; init; } = [];

    /// <summary>消息创建时间（界面显示 HH:mm）。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>助手消息的生成耗时（毫秒）；用户消息为 0。</summary>
    public long DurationMs { get; set; }

    /// <summary>本次请求的输入 token（助手消息）。</summary>
    public int InputTokens { get; set; }

    /// <summary>本次请求的输出 token（助手消息）。</summary>
    public int OutputTokens { get; set; }

    /// <summary>本次生成里各工具的调用次数，例如 web_search → 2。</summary>
    public Dictionary<string, int> ToolCalls { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮由哪个插件生成；普通对话为 null。持久化到 PluginJson。</summary>
    public PluginMessageInfo? Plugin { get; set; }

    /// <summary>
    /// 挂在这条消息下的派生卡片（插件对它的加工结果）。
    /// 持久化到 DerivedJson，但<b>不进入后续请求上下文</b>。
    /// </summary>
    public List<DerivedArtifact> Derived { get; init; } = [];

    /// <summary>
    /// pin 的消息永不被裁剪淘汰（数量上限见 <see cref="ContextSettings.MaxPinnedMessages"/>）。
    /// P1 只落字段与规则，完整 pin 交互在 P2。
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>本条助手消息的工具轨迹；持久化到 ToolTraceJson，回放时压成紧凑行。</summary>
    public List<Context.ToolTraceEntry> ToolTrace { get; init; } = [];

    /// <summary>
    /// M3：本轮从工作区知识库引用的归档会话；只用于界面跳转，
    /// 与派生卡片一样<b>不进入后续请求上下文</b>。持久化到 MemoryCitationsJson。
    /// </summary>
    public List<Context.MemoryCitation> MemoryCitations { get; init; } = [];

    public int TotalTokens => InputTokens + OutputTokens;
}
