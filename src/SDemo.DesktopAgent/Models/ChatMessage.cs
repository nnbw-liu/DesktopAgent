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
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public ChatMessageRole Role { get; } = role;

    public string Content { get; set; } = content;

    /// <summary>思维链（reasoning_content），为空表示模型没有返回。</summary>
    public string Reasoning { get; set; } = string.Empty;
}
