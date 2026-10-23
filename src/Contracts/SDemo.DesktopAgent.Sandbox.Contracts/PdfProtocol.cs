using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Sandbox.Protocol;

/// <summary>
/// SandboxHost 原子写入 <c>output/pdf.json</c> 的解析结果。
/// 解析失败 / 加密 / 无文本层都是“正常结果”，用 <see cref="Ok"/> / <see cref="Encrypted"/> /
/// <see cref="HasTextLayer"/> 表达；只有协议层错误才让宿主按 ProtocolError 处理。
/// </summary>
public sealed record PdfResultDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("pageCount")]
    public int PageCount { get; init; }

    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; init; }

    [JsonPropertyName("hasTextLayer")]
    public bool HasTextLayer { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("pages")]
    public IReadOnlyList<PdfPageText> Pages { get; init; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    // 协议字段放在最后：manifest / 诊断用的 jobId 与版本必须显式校验，不能只靠内容判断归属。

    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = SandboxProtocol.Version;
}
