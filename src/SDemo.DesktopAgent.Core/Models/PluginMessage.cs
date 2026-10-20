namespace SDemo.DesktopAgent.Models;

/// <summary>对照模式下的一对「原文 / 译文」。</summary>
public sealed record OutputSegment(string Source, string Target);

/// <summary>
/// 一条助手消息由哪个插件生成（随消息持久化到 messages.PluginJson）。
/// 插件被卸载后这里仍然可读——它是快照，不是对插件的引用。
/// </summary>
public sealed record PluginMessageInfo(
    string PluginId,
    string MountId,
    string CapabilityId,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<OutputSegment>? Segments)
{
    /// <summary>没有段落结构（纯文本模式）或模型没按格式返回时为 false。不参与序列化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSegments => Segments is { Count: > 0 };
}
