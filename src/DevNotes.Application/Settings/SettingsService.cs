namespace DevNotes.Application.Settings;

public sealed class SettingsService(ISettingsStore store) : ISettingsService, IDisposable
{
    private readonly ISettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = new();

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, loaded.Normalize());
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, Current);
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        AppSettings updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _gate.Release();
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    public void Dispose() => _gate.Dispose();
}
