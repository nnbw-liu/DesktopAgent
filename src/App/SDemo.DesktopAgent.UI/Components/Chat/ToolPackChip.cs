namespace SDemo.DesktopAgent.Components.Chat;

/// <summary>
/// 输入框里的工具包 chip。由 ChatRoom 根据 <c>AgentToolRegistry</c> + 沙箱可用性计算，
/// 不在这里判断平台或沙箱状态。
/// </summary>
public sealed record ToolPackChip(
    string Id,
    string Name,
    string Description,
    string Glyph,
    bool Enabled,
    bool Available,
    string? Reason)
{
    /// <summary>hover 提示：不可用时优先说明原因。</summary>
    public string Title => Available
        ? $"{Name}：{Description}（{(Enabled ? "已启用" : "点击启用")}）"
        : $"{Name}不可用：{Reason}";
}
