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
    IReadOnlyList<EditedFile>? Edits = null,
    // 这次工具调用发生时，已经产生了多少思维链 / 正文字符。用于重开会话时还原本「思考→工具→思考」的步骤顺序；
    // -1 表示旧记录没有这个信息。
    int ReasoningChars = -1,
    int TextChars = -1,
    // 本次工具调用的关联 id；运行中的条目在结果返回后按它原地替换。
    string? CallId = null)
{
    public const string StatusRunning = "running";
    public const string StatusOk = "ok";
    public const string StatusError = "error";
    public const string StatusRejected = "rejected";
    public const string StatusDenied = "denied";
    public const string StatusCanceled = "canceled";
}

/// <summary>工具轨迹里的一次文件改动（供「本次改动」面板汇总；不额外扫描磁盘）。</summary>
public sealed record EditedFile(string Path, int Added, int Removed);
