using DevNotes.Application.Abstractions;
using DevNotes.Application.Indexing;
using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevNotes.Application.Vaults;

public interface IVaultSessionFactory
{
    /// <summary>Creates a session that has not been started yet.</summary>
    IVaultSession Create(Vault vault);
}

public sealed class VaultSessionFactory(
    INoteFileStoreFactory fileStores,
    INoteIndexFactory indexes,
    IVaultWatcherFactory watchers,
    INoteIdGenerator idGenerator,
    TimeProvider timeProvider,
    IOptions<IndexingOptions> options,
    ILoggerFactory loggerFactory) : IVaultSessionFactory
{
    public IVaultSession Create(Vault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);

        var index = indexes.Open(vault.Id);
        IVaultWatcher? watcher = null;
        try
        {
            watcher = watchers.Create(vault.RootPath);
            return new VaultSession(
                vault,
                fileStores.Create(vault.RootPath),
                index,
                watcher,
                idGenerator,
                timeProvider,
                options.Value,
                loggerFactory);
        }
        catch
        {
            watcher?.Dispose();

            // Nothing asynchronous has started yet: releasing the index synchronously is safe here.
            index.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}

/// <summary>Owns the session of the vault that is currently open (one at a time).</summary>
public interface IVaultSessionManager : IAsyncDisposable
{
    IVaultSession? Current { get; }

    /// <summary>Raised after the open vault changed (null when it was closed). May be raised on any thread.</summary>
    event EventHandler<IVaultSession?>? CurrentChanged;

    /// <summary>Closes the current vault, if any, and opens <paramref name="vault"/>.</summary>
    Task<IVaultSession> OpenAsync(Vault vault, CancellationToken cancellationToken);

    Task CloseAsync();
}

public sealed class VaultSessionManager(IVaultSessionFactory factory) : IVaultSessionManager
{
    private readonly IVaultSessionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IVaultSession? _current;
    private bool _disposed;
    private int _disposeStarted;

    public event EventHandler<IVaultSession?>? CurrentChanged;

    public IVaultSession? Current => Volatile.Read(ref _current);

    public async Task<IVaultSession> OpenAsync(Vault vault, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vault);

        IVaultSession session;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await CloseCurrentAsync().ConfigureAwait(false);

            session = _factory.Create(vault);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Volatile.Write(ref _current, session);
        }
        finally
        {
            _gate.Release();
        }

        CurrentChanged?.Invoke(this, session);
        return session;
    }

    public async Task CloseAsync()
    {
        bool closed;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            closed = await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (closed)
        {
            CurrentChanged?.Invoke(this, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<bool> CloseCurrentAsync()
    {
        var previous = Interlocked.Exchange(ref _current, null);
        if (previous is null)
        {
            return false;
        }

        await previous.DisposeAsync().ConfigureAwait(false);
        return true;
    }
}
