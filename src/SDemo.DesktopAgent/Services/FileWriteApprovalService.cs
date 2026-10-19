namespace SDemo.DesktopAgent.Services;

/// <summary>模型请求写入文件时的确认请求。</summary>
public sealed record FileWriteRequest(string Path, string Content);

/// <summary>
/// 文件写入确认。工具在写文件前 await 这里，界面弹出确认框后由 <see cref="Resolve"/> 决定是否放行。
/// </summary>
public sealed class FileWriteApprovalService
{
    private TaskCompletionSource<bool>? _pending;

    /// <summary>当前等待用户确认的写入请求；为空表示没有待确认项。</summary>
    public FileWriteRequest? Pending { get; private set; }

    /// <summary>待确认状态变化（可能来自后台线程，订阅方需要自行切回 UI 线程）。</summary>
    public event Action? Changed;

    public Task<bool> RequestAsync(FileWriteRequest request, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending = completion;
        Pending = request;
        Changed?.Invoke();

        if (cancellationToken.CanBeCanceled)
        {
            // 用户点了「停止生成」：视为拒绝。
            cancellationToken.Register(() => Resolve(false));
        }

        return completion.Task;
    }

    public void Resolve(bool approved)
    {
        var completion = _pending;

        _pending = null;
        Pending = null;
        Changed?.Invoke();

        completion?.TrySetResult(approved);
    }
}
