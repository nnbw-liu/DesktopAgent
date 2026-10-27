namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 一次技能脚本运行请求。宿主执行器只接受已经由技能库解析、校验过的绝对路径；
/// 模型始终只看到技能名与包内相对路径。
/// </summary>
public sealed record SkillScriptRequest(
    string SkillName,
    string SkillSource,
    string ScriptPath,
    string ScriptRelativePath,
    string ScriptSha256,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaxOutputChars);

/// <summary>
/// 技能脚本宿主环境：Node 是否可用、解析到的路径与版本，以及浏览器路径（Archify 的
/// browser-check 需要）。<see cref="Detail"/> 是给用户看的一句话状态。
/// </summary>
public sealed record SkillScriptHostInfo(
    bool NodeAvailable,
    string? NodePath,
    string? NodeVersion,
    string? ChromePath,
    string? Detail)
{
    public static SkillScriptHostInfo Unavailable(string detail) =>
        new(false, null, null, null, detail);
}

/// <summary>
/// 技能脚本运行结果；字段与直跑器的结果对齐，但不把平台类型泄露给工具层。
/// </summary>
public sealed record SkillScriptRunResult(
    bool NodeAvailable,
    string? NodePath,
    string? NodeVersion,
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Canceled,
    long DurationMs,
    bool StdOutTruncated,
    bool StdErrTruncated,
    string? Error = null)
{
    public bool Succeeded =>
        Error is null && NodeAvailable && !TimedOut && !Canceled && ExitCode == 0;

    public static SkillScriptRunResult Failed(
        SkillScriptHostInfo host,
        string error,
        long durationMs = 0) =>
        new(host.NodeAvailable, host.NodePath, host.NodeVersion, -1, string.Empty, string.Empty,
            TimedOut: false, Canceled: false, durationMs, false, false, error);
}

/// <summary>
/// 技能脚本执行器。桌面（Windows）实现解析 Node / Edge 并走宿主直跑；
/// 其他平台注册一个不可用实现，让工具给出可恢复的提示。
/// </summary>
public interface ISkillScriptRunner
{
    /// <summary>当前宿主环境；构造后即可读，不启动进程。</summary>
    SkillScriptHostInfo Host { get; }

    Task<SkillScriptRunResult> RunAsync(
        SkillScriptRequest request,
        CancellationToken cancellationToken = default);
}
