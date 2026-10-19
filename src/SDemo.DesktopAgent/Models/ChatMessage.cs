namespace SDemo.DesktopAgent.Models;

/// <summary>消息角色。</summary>
public enum ChatMessageRole
{
    User,
    Assistant
}

/// <summary>一条聊天消息。助手消息在流式生成过程中会不断追加内容。</summary>
public sealed class ChatMessage(ChatMessageRole role, string content)
{
    public ChatMessageRole Role { get; } = role;

    public string Content { get; set; } = content;
}
