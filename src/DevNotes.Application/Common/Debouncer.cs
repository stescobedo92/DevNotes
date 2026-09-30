using System.Diagnostics.CodeAnalysis;

namespace DevNotes.Application.Common;

/// <summary>
/// Runs an asynchronous action once a burst of signals has been quiet for a given delay.
/// Runs never overlap; a signal that arrives while the action is running schedules another run.
/// Time is driven by a <see cref="TimeProvider"/> so behaviour is deterministic under test.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly Lock _gate = new();

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "A run in flight must still be able to release it after Dispose; no wait handle is ever allocated.")]
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Func<CancellationToken, Task> _action;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _disposal = new();
    private readonly ITimer _timer;
    private readonly TimeSpan _delay;
    private bool _pending;
    private bool _disposed;

    /// <param name="timeProvider">Clock used to schedule the run.</param>
    /// <param name="delay">Quiet period required before the action runs.</param>
    /// <param name="action">Work to run; it may be invoked on a thread-pool thread.</param>
    /// <param name="onError">Receives any exception thrown by <paramref name="action"/> so failures are never silent.</param>
    public Debouncer(TimeProvider timeProvider, TimeSpan delay, Func<CancellationToken, Task> action, Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        _action = action ?? throw new ArgumentNullException(nameof(action));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _delay = delay;
        _timer = timeProvider.CreateTimer(OnTimerElapsed, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True when a signal is waiting for its run.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Registers activity and (re)starts the quiet period.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops the pending run, if any. A run that already started is not interrupted.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs the pending work now (if any) and waits for it, including a run already in progress.</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        return RunPendingAsync();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = false;
        }

        _disposal.Cancel();
        _timer.Dispose();
        _disposal.Dispose();
    }

    private void OnTimerElapsed(object? state) => _ = RunPendingAsync();

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The action is user code running on a timer thread; every failure is routed to the error handler.")]
    private async Task RunPendingAsync()
    {
        CancellationToken token;
        try
        {
            token = _disposal.Token;
            await _runLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            bool run;
            lock (_gate)
            {
                run = _pending && !_disposed;
                _pending = false;
            }

            if (run)
            {
                await _action(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposed while running: nothing to report.
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
        finally
        {
            _runLock.Release();
        }
    }
}
