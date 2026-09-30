using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using DevNotes.Application.Abstractions;
using DevNotes.Application.Common;
using DevNotes.Application.Indexing;
using DevNotes.Application.Notes;
using DevNotes.Application.Search;
using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;
using Microsoft.Extensions.Logging;

namespace DevNotes.Application.Vaults;

/// <summary>
/// Everything that is alive while a vault is open: its files, its index, the file watcher and
/// the background worker that keeps the index in sync.
/// </summary>
public interface IVaultSession : IAsyncDisposable
{
    Vault Vault { get; }

    INoteService Notes { get; }

    INoteQueryService Queries { get; }

    VaultEventHub Events { get; }

    /// <summary>Prepares the index, starts watching the folder and queues the initial incremental scan.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Queues an incremental scan of the vault.</summary>
    void RequestSynchronize();

    /// <summary>Queues a full rebuild of the index ("Reindex all").</summary>
    void RequestRebuild();

    /// <summary>Completes when every change reported so far has been indexed.</summary>
    Task WhenIdleAsync(CancellationToken cancellationToken);
}

public sealed partial class VaultSession : IVaultSession
{
    private readonly INoteIndex _index;
    private readonly IVaultWatcher _watcher;
    private readonly VaultIndexer _indexer;
    private readonly IndexingOptions _options;
    private readonly ILogger _logger;
    private readonly Debouncer _debouncer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<IndexJob> _jobs = Channel.CreateUnbounded<IndexJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Lock _bufferGate = new();
    private readonly Dictionary<NotePath, bool> _bufferedChanges = [];
    private bool _rescanBuffered;
    private int _scanQueued;
    private Task? _worker;
    private int _disposed;

    public VaultSession(
        Vault vault,
        INoteFileStore files,
        INoteIndex index,
        IVaultWatcher watcher,
        INoteIdGenerator idGenerator,
        TimeProvider timeProvider,
        IndexingOptions options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        Vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = loggerFactory.CreateLogger<VaultSession>();

        Events = new VaultEventHub();
        _indexer = new VaultIndexer(files, index, options, loggerFactory.CreateLogger<VaultIndexer>());
        Notes = new NoteService(files, _indexer, Events, idGenerator, timeProvider, loggerFactory.CreateLogger<NoteService>());
        Queries = new NoteQueryService(index);
        _debouncer = new Debouncer(timeProvider, options.WatcherDebounce, FlushBufferedChangesAsync, LogDebouncerFailed);
    }

    public Vault Vault { get; }

    public INoteService Notes { get; }

    public INoteQueryService Queries { get; }

    public VaultEventHub Events { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_worker is not null)
        {
            throw new InvalidOperationException("The vault session was already started.");
        }

        await _index.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _worker = Task.Run(() => RunWorkerAsync(_shutdown.Token), CancellationToken.None);

        _watcher.Changed += OnWatcherChanged;
        try
        {
            _watcher.Start();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // The app stays usable without live updates: scans still pick up external changes.
            LogWatcherUnavailable(exception, Vault.RootPath);
        }

        RequestSynchronize();
    }

    public void RequestSynchronize()
    {
        if (Interlocked.Exchange(ref _scanQueued, 1) == 0)
        {
            Enqueue(IndexJob.Scan(rebuild: false));
        }
    }

    public void RequestRebuild() => Enqueue(IndexJob.Scan(rebuild: true));

    public async Task WhenIdleAsync(CancellationToken cancellationToken)
    {
        await _debouncer.FlushAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_jobs.Writer.TryWrite(IndexJob.Barrier(barrier)))
        {
            return; // Session closed: nothing left to wait for.
        }

        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.Changed -= OnWatcherChanged;
        _watcher.Dispose();
        _debouncer.Dispose();

        _jobs.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the worker was cancelled by this disposal.
            }
        }

        // Unblock anything still waiting on a barrier that will never be processed.
        while (_jobs.Reader.TryRead(out var job))
        {
            job.Completion?.TrySetResult();
        }

        await _index.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void Enqueue(IndexJob job)
    {
        if (!_jobs.Writer.TryWrite(job))
        {
            job.Completion?.TrySetResult();
        }
    }

    private void OnWatcherChanged(object? sender, VaultFileEvent e)
    {
        lock (_bufferGate)
        {
            switch (e.Kind)
            {
                case VaultFileEventKind.Overflow:
                    _rescanBuffered = true;
                    break;
                case VaultFileEventKind.Created:
                case VaultFileEventKind.Changed:
                    BufferChange(e.RelativePath, removed: false, structural: e.Kind == VaultFileEventKind.Created);
                    break;
                case VaultFileEventKind.Deleted:
                    BufferChange(e.RelativePath, removed: true, structural: true);
                    break;
                case VaultFileEventKind.Renamed:
                    if (e.OldRelativePath is not null)
                    {
                        BufferChange(e.OldRelativePath, removed: true, structural: true);
                    }

                    BufferChange(e.RelativePath, removed: false, structural: true);
                    break;
                default:
                    return;
            }

            if (_bufferedChanges.Count > _options.EffectiveMaxBufferedChanges)
            {
                _bufferedChanges.Clear();
                _rescanBuffered = true;
            }
        }

        _debouncer.Signal();
    }

    /// <param name="relativePath">Path reported by the watcher.</param>
    /// <param name="removed">Whether the path disappeared.</param>
    /// <param name="structural">True for create / delete / rename, which may refer to a whole folder.</param>
    private void BufferChange(string relativePath, bool removed, bool structural)
    {
        if (IsHidden(relativePath))
        {
            return; // .git, .devnotes, editor swap files and our own temporary files.
        }

        if (NotePath.TryCreate(relativePath, out var path))
        {
            _bufferedChanges[path] = removed;
        }
        else if (structural)
        {
            // Not a note: probably a folder that was created, moved or deleted with notes inside.
            _rescanBuffered = true;
        }
    }

    private static bool IsHidden(string relativePath) =>
        relativePath.StartsWith('.')
        || relativePath.Contains("/.", StringComparison.Ordinal)
        || relativePath.Contains("\\.", StringComparison.Ordinal);

    private Task FlushBufferedChangesAsync(CancellationToken cancellationToken)
    {
        bool rescan;
        KeyValuePair<NotePath, bool>[] changes;
        lock (_bufferGate)
        {
            rescan = _rescanBuffered;
            changes = [.. _bufferedChanges];
            _rescanBuffered = false;
            _bufferedChanges.Clear();
        }

        if (rescan)
        {
            RequestSynchronize();
        }
        else if (changes.Length > 0)
        {
            Enqueue(IndexJob.Changes(changes));
        }

        return Task.CompletedTask;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The worker must survive any failure of a single job; the failure is logged and published as index status.")]
    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                job.Completion?.TrySetResult();
                throw;
            }
            catch (Exception exception)
            {
                LogJobFailed(exception, job.Kind.ToString());
                Events.PublishStatus(new IndexStatus(IndexState.Failed, default, exception.Message));
            }

            job.Completion?.TrySetResult();
        }
    }

    private async Task ProcessAsync(IndexJob job, CancellationToken cancellationToken)
    {
        switch (job.Kind)
        {
            case IndexJobKind.Scan:
            case IndexJobKind.Rebuild:
                Interlocked.Exchange(ref _scanQueued, 0);
                Events.PublishStatus(new IndexStatus(IndexState.Indexing, default, null));
                var progress = new InlineProgress(p => Events.PublishStatus(new IndexStatus(IndexState.Indexing, p, null)));
                if (job.Kind == IndexJobKind.Rebuild)
                {
                    await _indexer.RebuildAsync(progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _indexer.SynchronizeAsync(progress, cancellationToken).ConfigureAwait(false);
                }

                Events.PublishStatus(IndexStatus.Idle);
                Events.PublishNotesChanged(NotesChangeSource.Scan);
                break;

            case IndexJobKind.Changes:
                var removed = new List<NotePath>();
                var affected = new List<NotePath>(job.ChangedPaths.Length);
                foreach (var (path, wasRemoved) in job.ChangedPaths)
                {
                    affected.Add(path);
                    if (wasRemoved)
                    {
                        removed.Add(path);
                    }
                    else
                    {
                        // Also handles a file that vanished again in the meantime.
                        await _indexer.IndexFileAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                }

                await _indexer.RemoveAsync(removed, cancellationToken).ConfigureAwait(false);
                Events.PublishStatus(IndexStatus.Idle);
                Events.PublishNotesChanged(NotesChangeSource.External, affected);
                break;

            default:
                break; // Barrier: completed by the worker loop.
        }
    }

    private enum IndexJobKind
    {
        Scan,
        Rebuild,
        Changes,
        Barrier,
    }

    private sealed record IndexJob(IndexJobKind Kind, KeyValuePair<NotePath, bool>[] ChangedPaths, TaskCompletionSource? Completion)
    {
        public static IndexJob Scan(bool rebuild) => new(rebuild ? IndexJobKind.Rebuild : IndexJobKind.Scan, [], null);

        public static IndexJob Changes(KeyValuePair<NotePath, bool>[] changes) => new(IndexJobKind.Changes, changes, null);

        public static IndexJob Barrier(TaskCompletionSource completion) => new(IndexJobKind.Barrier, [], completion);
    }

    /// <summary>Reports synchronously on the calling thread (no SynchronizationContext capture, unlike Progress&lt;T&gt;).</summary>
    private sealed class InlineProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Error, Message = "Index job '{Job}' failed")]
    private partial void LogJobFailed(Exception exception, string job);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "File watching is unavailable for '{Root}'; external changes are picked up by scans only")]
    private partial void LogWatcherUnavailable(Exception exception, string root);

    [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "Buffered vault changes could not be queued")]
    private partial void LogDebouncerFailed(Exception exception);
}
