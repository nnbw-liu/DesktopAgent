namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 没有系统 Shell 的宿主（测试 / 无桌面环境）用的兜底实现：一律返回 false，
/// 界面只提示「当前环境不支持打开文件」，不会抛异常。
/// </summary>
public sealed class NullFileLauncherService : IFileLauncherService
{
    public Task<bool> OpenFileAsync(string path) => Task.FromResult(false);

    public Task<bool> RevealFileAsync(string path) => Task.FromResult(false);
}
