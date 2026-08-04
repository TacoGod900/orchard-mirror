namespace Orchard.Runtime.Windows;

/// <summary>A semantic UI intent that is resolved against the current render when it is dispatched.</summary>
public sealed record LiveSemanticIntent(string NodeId, string Event, string? Value = null)
{
    public bool IsCoalescible => string.Equals(Event, "change", StringComparison.Ordinal);
}

public enum LiveIntentEnqueueResult
{
    Enqueued,
    Coalesced,
    Rejected
}

/// <summary>
/// Serializes semantic UI intents without blocking the UI thread. Pending change intents for the same
/// node are coalesced only within their current ordering segment; presses and submits are never dropped.
/// </summary>
public sealed class LiveSemanticIntentScheduler : IAsyncDisposable
{
    private const int DefaultMaximumPendingIntents = 256;
    private readonly object _gate = new();
    private readonly LinkedList<LiveSemanticIntent> _pending = new();
    private readonly Func<LiveSemanticIntent, CancellationToken, Task<LiveRenderUpdate>> _dispatch;
    private readonly Func<LiveSemanticIntent, LiveRenderUpdate, Task> _publish;
    private readonly Func<LiveSemanticIntent, Exception, Task> _publishError;
    private readonly CancellationTokenSource _stop = new();
    private readonly int _maximumPendingIntents;
    private Task _pumpTask = Task.CompletedTask;
    private TaskCompletionSource _idle = CompletedSource();
    private bool _pumpRunning;
    private bool _accepting = true;
    private Exception? _terminalObserverError;
    private int _disposed;

    public LiveSemanticIntentScheduler(
        Func<LiveSemanticIntent, CancellationToken, Task<LiveRenderUpdate>> dispatch,
        Func<LiveSemanticIntent, LiveRenderUpdate, Task> publish,
        Func<LiveSemanticIntent, Exception, Task> publishError,
        int maximumPendingIntents = DefaultMaximumPendingIntents)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _publishError = publishError ?? throw new ArgumentNullException(nameof(publishError));
        if (maximumPendingIntents is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingIntents));
        }

        _maximumPendingIntents = maximumPendingIntents;
    }

    public LiveIntentEnqueueResult Enqueue(LiveSemanticIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.NodeId) || string.IsNullOrWhiteSpace(intent.Event))
        {
            throw new ArgumentException("A semantic intent requires a node identifier and event.", nameof(intent));
        }

        lock (_gate)
        {
            if (!_accepting || Volatile.Read(ref _disposed) != 0)
            {
                return LiveIntentEnqueueResult.Rejected;
            }

            if (intent.IsCoalescible && TryCoalesceTail(intent))
            {
                return LiveIntentEnqueueResult.Coalesced;
            }

            if (_pending.Count >= _maximumPendingIntents)
            {
                return LiveIntentEnqueueResult.Rejected;
            }

            _pending.AddLast(intent);
            if (!_pumpRunning)
            {
                _pumpRunning = true;
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pumpTask = PumpAsync();
            }

            return LiveIntentEnqueueResult.Enqueued;
        }
    }

    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }

        return idle.WaitAsync(cancellationToken);
    }

    public Exception? TerminalObserverError
    {
        get
        {
            lock (_gate)
            {
                return _terminalObserverError;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task pump;
        lock (_gate)
        {
            _accepting = false;
            _pending.Clear();
            _stop.Cancel();
            pump = _pumpTask;
        }

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Cancellation is the queue's normal shutdown path.
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private bool TryCoalesceTail(LiveSemanticIntent replacement)
    {
        var candidate = _pending.Last;
        if (candidate is not null &&
            candidate.Value.IsCoalescible &&
            string.Equals(candidate.Value.NodeId, replacement.NodeId, StringComparison.Ordinal) &&
            string.Equals(candidate.Value.Event, replacement.Event, StringComparison.Ordinal))
        {
            candidate.Value = replacement;
            return true;
        }

        return false;
    }

    private async Task PumpAsync()
    {
        // Ensure Enqueue returns before dispatch begins, so adjacent WinForms focus/click events can queue in order.
        await Task.Yield();
        while (true)
        {
            LiveSemanticIntent intent;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || _pending.First is null)
                {
                    _pumpRunning = false;
                    _idle.TrySetResult();
                    return;
                }

                intent = _pending.First.Value;
                _pending.RemoveFirst();
            }

            try
            {
                var update = await _dispatch(intent, _stop.Token);
                await _publish(intent, update);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _pumpRunning = false;
                    _pending.Clear();
                    _idle.TrySetResult();
                }
                return;
            }
            catch (Exception exception)
            {
                try
                {
                    await _publishError(intent, exception);
                }
                catch (Exception observerException)
                {
                    lock (_gate)
                    {
                        _terminalObserverError = observerException;
                        _accepting = false;
                        _pending.Clear();
                        _pumpRunning = false;
                        _idle.TrySetResult();
                    }
                    return;
                }
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
