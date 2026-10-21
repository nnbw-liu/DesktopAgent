using System.IO;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// AppContainer profile（S0 占位）。
/// S1 实现 <c>CreateAppContainerProfile</c> / <c>DeriveAppContainerSidFromAppContainerName</c> /
/// <c>DeleteAppContainerProfile</c>，并在这里暴露 Package SID 给 ACL 与 CreateProcess 使用。
/// </summary>
public sealed class AppContainerProfile : IDisposable
{
    public const string ProfileName = "SDemo.DesktopAgent.Sandbox";

    /// <summary>
    /// S1 之前调用属于误用：占位实现直接抛出，避免一个假 SID 被当成真实隔离。
    /// </summary>
    public static AppContainerProfile Ensure() =>
        throw new NotSupportedException("AppContainer profile 在 S1 阶段实现。");

    public void Dispose()
    {
    }
}
