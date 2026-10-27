namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 非 Windows 宿主的占位执行器：技能脚本只能在桌面宿主直跑，
/// Android / Linux 上让工具返回可恢复的提示，而不是暴露一个静默失败。
/// </summary>
public sealed class UnavailableSkillScriptRunner : ISkillScriptRunner
{
    public const string UnavailableMessage = "技能脚本运行器只在 Windows 桌面宿主可用。";

    public SkillScriptHostInfo Host { get; } =
        SkillScriptHostInfo.Unavailable(UnavailableMessage);

    public Task<SkillScriptRunResult> RunAsync(
        SkillScriptRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SkillScriptRunResult.Failed(Host, UnavailableMessage));
}
