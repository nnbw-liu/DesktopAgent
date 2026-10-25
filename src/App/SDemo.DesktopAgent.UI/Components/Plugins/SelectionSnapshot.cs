using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Components.Plugins;

/// <summary>
/// 一次划词选区的快照（由 <c>sdChat.readSelection</c> 返回）。
/// 坐标是视口坐标：浮动条用 <c>position: fixed</c> 渲染，滚动即关闭。
/// </summary>
public sealed record SelectionSnapshot(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("top")] double Top,
    [property: JsonPropertyName("bottom")] double Bottom,
    [property: JsonPropertyName("placeAbove")] bool PlaceAbove);
