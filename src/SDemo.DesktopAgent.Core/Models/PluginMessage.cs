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

/// <summary>派生卡片的状态。</summary>
public enum DerivedState
{
    Running,
    Done,
    Canceled,
    Failed
}

/// <summary>
/// 派生卡片：对既有消息的一次加工结果，挂在原消息下。
/// 它是<b>文本快照</b>——插件卸载后仍可读，也<b>不会进入后续上下文</b>。
/// </summary>
public sealed record DerivedArtifact(
    string Id,
    string PluginId,
    string MountId,
    string CapabilityId,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<OutputSegment>? Segments,
    string Text,
    DerivedState State,
    string? Error,
    string ModelId,
    long DurationMs,
    int InputTokens,
    int OutputTokens,
    DateTime CreatedAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalTokens => InputTokens + OutputTokens;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRunning => State == DerivedState.Running;
}

