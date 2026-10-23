namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 进程内单例的 profile 提供者：首次使用时尝试创建，失败原因缓存下来。
/// 不能在 DI 构造期抛异常——那会把设置页一起拖垮，应该让自检把原因展示出来。
/// </summary>
public sealed class AppContainerProfileProvider : IDisposable
{
    private readonly object _gate = new();
    private AppContainerProfile? _profile;
    private string? _reason;
    private bool _attempted;

    /// <summary>已创建的 profile；从未尝试或创建失败时返回 null，并通过 <paramref name="reason"/> 给出原因。</summary>
    public AppContainerProfile? TryGet(out string? reason)
    {
        lock (_gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                try
                {
                    _profile = AppContainerProfile.Ensure();
                }
                catch (Exception ex) when (ex is AppContainerUnavailableException or UnauthorizedAccessException)
                {
                    _reason = ex.Message;
                }
            }

            reason = _reason;
            return _profile;
        }
    }

    /// <summary>丢弃缓存结果（例如用户修复系统策略后点击“重新自检”）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _profile?.Dispose();
            _profile = null;
            _reason = null;
            _attempted = false;
        }
    }

    public void Dispose() => Reset();
}
