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
    /// <summary>Times a changed file is looked at before giving up until the next scan.</summary>
    internal const int MaxChangeAttempts = 3;

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
    private readonly HashSet<NotePath> _bufferedChanges = [];

    // Only the worker touches it.
    private readonly Dictionary<NotePath, int> _failedAttempts = [];
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

        Events = new VaultEventHub(LogSubscriberFailed);
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
                    BufferChange(e.RelativePath, structural: e.Kind == VaultFileEventKind.Created);
                    break;
                case VaultFileEventKind.Deleted:
                    BufferChange(e.RelativePath, structural: true);
                    break;
                case VaultFileEventKind.Renamed:
                    if (e.OldRelativePath is not null)
                    {
                        BufferChange(e.OldRelativePath, structural: true);
                    }

                    BufferChange(e.RelativePath, structural: true);
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
    /// <param name="structural">True for create / delete / rename, which may refer to a whole folder.</param>
    private void BufferChange(string relativePath, bool structural)
    {
        if (IsHidden(relativePath))
        {
            return; // .git, .devnotes, editor swap files and our own temporary files.
        }

        if (NotePath.TryCreate(relativePath, out var path))
        {
            _bufferedChanges.Add(path);
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
        NotePath[] changes;
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
                // Events only say "look at this path": what happens is decided by the current state of the
                // file (indexed if it exists, removed if it does not). A stale "deleted" event therefore
                // can never evict a note that was restored or rewritten in the meantime.
                List<NotePath>? retry = null;
                foreach (var path in job.ChangedPaths)
                {
                    try
                    {
                        await _indexer.IndexFileAsync(path, cancellationToken).ConfigureAwait(false);
                        _failedAttempts.Remove(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        // Usually the program that is saving the note still holds it open. One unreadable file
                        // must not hide the other changes of the batch; it is looked at again after the next
                        // quiet period, a few times at most (the next scan picks up whatever is left).
                        var attempts = _failedAttempts.GetValueOrDefault(path) + 1;
                        LogChangeNotIndexed(exception, path.Value, attempts);
                        if (attempts < MaxChangeAttempts)
                        {
                            _failedAttempts[path] = attempts;
                            (retry ??= []).Add(path);
                        }
                        else
                        {
                            _failedAttempts.Remove(path);
                        }
                    }
                }

                Events.PublishStatus(IndexStatus.Idle);
                Events.PublishNotesChanged(NotesChangeSource.External, job.ChangedPaths);
                if (retry is not null)
                {
                    lock (_bufferGate)
                    {
                        _bufferedChanges.UnionWith(retry);
                    }

                    _debouncer.Signal();
                }

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

    private sealed record IndexJob(IndexJobKind Kind, NotePath[] ChangedPaths, TaskCompletionSource? Completion)
    {
        public static IndexJob Scan(bool rebuild) => new(rebuild ? IndexJobKind.Rebuild : IndexJobKind.Scan, [], null);

        public static IndexJob Changes(NotePath[] changes) => new(IndexJobKind.Changes, changes, null);

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

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "The changed note '{Path}' could not be read (attempt {Attempt})")]
    private partial void LogChangeNotIndexed(Exception exception, string path, int attempt);

    [LoggerMessage(EventId = 304, Level = LogLevel.Error, Message = "A subscriber of the vault events failed")]
    private partial void LogSubscriberFailed(Exception exception);
}
