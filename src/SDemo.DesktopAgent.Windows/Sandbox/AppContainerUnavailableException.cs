namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>AppContainer 无法创建 / 不可用。调用方据此返回 NotAvailable，不要吞掉原因。</summary>
public sealed class AppContainerUnavailableException : Exception
{
    public AppContainerUnavailableException(string message) : base(message)
    {
    }

    public AppContainerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
