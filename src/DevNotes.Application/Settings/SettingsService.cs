namespace DevNotes.Application.Settings;

public sealed class SettingsService(ISettingsStore store) : ISettingsService, IAsyncDisposable
{
    private readonly ISettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = new();
    private int _pending;
    private int _disposed;

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, loaded.Normalize());
        }
        finally
        {
            Exit();
        }

        Changed?.Invoke(this, Current);
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        AppSettings updated;
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Current;
            updated = update(current).Normalize();
            if (updated == current)
            {
                return current;
            }

            // Persist first: memory must never claim a state that failed to reach the disk.
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, updated);
        }
        finally
        {
            Exit();
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    /// <summary>
    /// Waits for the operations that are queued or being written (the UI starts some updates without
    /// awaiting them) so closing the app never cuts a save short, then rejects any later call.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _pending) == 0)
            {
                _gate.Dispose();
                return;
            }

            // The semaphore does not promise arrival order: let the operations still queued go first.
            _gate.Release();
            await Task.Yield();
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _pending);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pending);
            throw;
        }
    }

    private void Exit()
    {
        Interlocked.Decrement(ref _pending);
        _gate.Release();
    }
}
