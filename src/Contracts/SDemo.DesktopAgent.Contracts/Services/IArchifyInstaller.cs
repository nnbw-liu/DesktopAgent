namespace SDemo.DesktopAgent.Services;

/// <summary>
/// Archify 3.2.0 的安装入口：把上游技能包复制到技能库，替换成 DesktopAgent 适配版
/// <c>SKILL.md</c>，并保留上游正文、许可与第三方声明。
/// </summary>
public interface IArchifyInstaller
{
    /// <summary>技能库里是否已经有 Archify 包。</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// 从上游目录安装。<paramref name="sourcePath"/> 可以是仓库根（含 <c>archify/</c>），
    /// 也可以是技能包目录（含 <c>SKILL.md</c> 与 <c>bin/archify.mjs</c>）。
    /// </summary>
    Task<ArchifyInstallResult> InstallAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

/// <summary>一次安装的结果；失败时只给用户一句话，不抛异常。</summary>
public sealed record ArchifyInstallResult(
    bool Success,
    string Message,
    string? TargetRoot = null,
    string? Version = null,
    string? Source = null,
    int Files = 0,
    long Bytes = 0)
{
    public static ArchifyInstallResult Fail(string message) => new(false, message);
}
