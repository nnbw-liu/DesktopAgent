namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一条工具调用轨迹（§3.6）。只存摘要与元信息，不存工具正文：
/// 这是「下一轮不失忆」的最小成本方案。
/// </summary>
public sealed record ToolTraceEntry(
    string ToolId,
    string Status,
    int? ExitCode,
    string? ArgsDigest,
    string? Summary,
    int OutputChars,
    DateTime At,
    IReadOnlyList<EditedFile>? Edits = null)
{
    public const string StatusOk = "ok";
    public const string StatusError = "error";
    public const string StatusRejected = "rejected";
    public const string StatusDenied = "denied";
    public const string StatusCanceled = "canceled";
}

/// <summary>工具轨迹里的一次文件改动（供「本次改动」面板汇总；不额外扫描磁盘）。</summary>
public sealed record EditedFile(string Path, int Added, int Removed);
