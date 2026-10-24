namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 宿主上下文快照的标识（P2a §3.3.1-(2)）。
/// <para>
/// 快照在传输层只能是一条 <c>user</c> 消息（多数 OpenAI 兼容端点不接受中途的 <c>system</c>），
/// 所以「这条 user 消息不是用户说的」必须靠正文前缀表达，并且所有「取最后一条用户输入」的
/// 逻辑都要显式把它排除 —— 否则路由会把「工作区 / 策略」当成用户这句话，把对话轮判成任务轮。
/// </para>
/// </summary>
public static class HostSnapshot
{
    /// <summary>快照标题行（不带换行），也用作识别前缀。</summary>
    public const string Title = "## 宿主上下文快照";

    /// <summary>快照消息的完整开头。</summary>
    public const string Header = Title + "（宿主生成，不是用户输入；本快照取代之前的快照）\n\n";

    /// <summary>这段文本是不是快照消息。</summary>
    public static bool IsSnapshot(string? text) =>
        text is not null && text.StartsWith(Title, StringComparison.Ordinal);

    /// <summary>把已渲染的段拼成一条快照消息正文。</summary>
    public static string Compose(string sections) => Header + sections;
}
