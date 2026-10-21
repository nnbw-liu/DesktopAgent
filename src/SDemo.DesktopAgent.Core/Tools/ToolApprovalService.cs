namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 串行审批队列。工具线程 await <see cref="RequestAsync"/>，界面在 <see cref="Changed"/> 后读
/// <see cref="Current"/> 并调用 <see cref="Resolve"/>；排队中的请求不会丢，也不会同时弹两个。
/// </summary>
public sealed class ToolApprovalService : IToolApprovalService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly HashSet<Entry> _entries = [];
    private Entry? _current;

    public ToolApprovalRequest? Current
    {
        get
        {
            lock (_sync)
            {
                return _current?.Request;
            }
        }
    }

    public event Action? Changed;

    public async Task<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entry = new Entry(request);

        lock (_sync)
        {
            _entries.Add(entry);
        }

        // 取消发生在排队 / 展示阶段都按拒绝处理，而不是把 OperationCanceledException 抛给模型。
        await using var registration = cancellationToken.Register(() =>
            entry.Completion.TrySetResult(ToolApprovalDecision.Deny));

        try
        {
            if (!await WaitForTurnAsync(cancellationToken).ConfigureAwait(false))
            {
                return ToolApprovalDecision.Deny;
            }

            try
            {
                // 等前一个请求期间就被取消 / CancelAll 了：不再抢占界面。
                if (entry.Completion.Task.IsCompleted)
                {
                    return await entry.Completion.Task.ConfigureAwait(false);
                }

                lock (_sync)
                {
                    _current = entry;
                }

                RaiseChanged();

                return await entry.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                var cleared = false;

                lock (_sync)
                {
                    if (ReferenceEquals(_current, entry))
                    {
                        _current = null;
                        cleared = true;
                    }
                }

                if (cleared)
                {
                    RaiseChanged();
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _entries.Remove(entry);

                // 排队期间被 CancelAll 的请求不会再显示，但如果它是最后一个，
                // 清理掉可能残留的 Current。
                if (ReferenceEquals(_current, entry))
                {
                    _current = null;
                }
            }

            _queue.Release();
        }
    }

    public void Resolve(ToolApprovalDecision decision)
    {
        Entry? entry;

        lock (_sync)
        {
            entry = _current;
        }

        entry?.Completion.TrySetResult(decision);
    }

    public void CancelAll()
    {
        Entry[] entries;

        lock (_sync)
        {
            entries = _entries.ToArray();
            _entries.Clear();
            _current = null;
        }

        foreach (var entry in entries)
        {
            entry.Completion.TrySetResult(ToolApprovalDecision.Deny);
        }

        RaiseChanged();
    }

    /// <summary>排队等待轮到自己；取消时返回 false（调用方按拒绝处理）。</summary>
    private async Task<bool> WaitForTurnAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // 审批界面的异常不能拖垮工具线程：请求仍然可以稍后由 CancelAll 结束。
        }
    }

    private sealed class Entry(ToolApprovalRequest request)
    {
        public ToolApprovalRequest Request { get; } = request;

        public TaskCompletionSource<ToolApprovalDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
