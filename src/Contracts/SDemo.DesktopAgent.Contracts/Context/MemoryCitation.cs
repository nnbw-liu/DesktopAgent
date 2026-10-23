namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一条知识库引用（M3）：来源会话 id、标题与时间。
/// 随助手消息落库，只用于界面「引用来源」跳转，不进入后续请求上下文。
/// </summary>
public sealed record MemoryCitation(string SessionId, string Title, DateTime At);
