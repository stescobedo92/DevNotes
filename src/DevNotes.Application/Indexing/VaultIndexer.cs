using System.Diagnostics;
using DevNotes.Application.Abstractions;
using DevNotes.Application.Notes;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Logging;

namespace DevNotes.Application.Indexing;

public interface IVaultIndexer
{
    /// <summary>
    /// Brings the index in line with the vault. Files whose size and modification time match the
    /// index are skipped; files that were only touched are detected by hash and not re-indexed.
    /// </summary>
    Task<IndexRunSummary> SynchronizeAsync(IProgress<IndexProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Drops the whole index and builds it again from the Markdown files.</summary>
    Task<IndexRunSummary> RebuildAsync(IProgress<IndexProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Indexes one file, or removes it from the index when it no longer exists.</summary>
    Task IndexFileAsync(NotePath path, CancellationToken cancellationToken);

    /// <summary>Indexes a file that was just read or written, without reading it again.</summary>
    Task IndexAsync(NoteFile file, CancellationToken cancellationToken);

    Task RemoveAsync(IReadOnlyCollection<NotePath> paths, CancellationToken cancellationToken);
}

public sealed partial class VaultIndexer : IVaultIndexer
{
    /// <summary>
    /// Bytes of note files read into memory per batch. Batches are also limited by number of notes,
    /// but a vault of a few hundred multi-megabyte files must not be loaded all at once.
    /// </summary>
    internal const long MaxBatchBytes = 32 * 1024 * 1024;

    private readonly INoteFileStore _files;
    private readonly INoteIndex _index;
    private readonly IndexingOptions _options;
    private readonly ILogger _logger;

    public VaultIndexer(INoteFileStore files, INoteIndex index, IndexingOptions options, ILogger<VaultIndexer> logger)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IndexRunSummary> RebuildAsync(IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        await _index.ClearAsync(cancellationToken).ConfigureAwait(false);
        return await SynchronizeAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IndexRunSummary> SynchronizeAsync(IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var onDisk = await _files.ListNotesAsync(cancellationToken).ConfigureAwait(false);
        var states = await _index.GetFileStatesAsync(cancellationToken).ConfigureAwait(false);

        var pathsOnDisk = new HashSet<NotePath>(onDisk.Count);
        var candidates = new List<NoteFileInfo>();
        foreach (var file in onDisk)
        {
            pathsOnDisk.Add(file.Path);
            if (!states.TryGetValue(file.Path, out var state)
                || state.FileSize != file.Size
                || state.FileLastWriteUtc != file.LastWriteTimeUtc)
            {
                candidates.Add(file);
            }
        }

        // Ids already owned by files that still exist; used to detect copies that share an id.
        var owners = new Dictionary<NoteId, NotePath>();
        foreach (var (path, state) in states)
        {
            if (pathsOnDisk.Contains(path))
            {
                owners[state.Id] = path;
            }
        }

        var indexed = 0;
        var touched = 0;
        var failed = 0;
        var processed = 0;
        progress?.Report(new IndexProgress(0, candidates.Count));

        foreach (var chunk in Batch(candidates, _options.EffectiveBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcomes = await ReadChunkAsync(chunk, states, cancellationToken).ConfigureAwait(false);

            var notes = new List<IndexedNote>(outcomes.Length);
            var touches = new List<NoteFileInfo>();
            foreach (var outcome in outcomes)
            {
                switch (outcome.Kind)
                {
                    case ReadKind.Changed:
                        notes.Add(ResolveIdentity(outcome.File!, outcome.Document!, states, owners));
                        break;
                    case ReadKind.Touched:
                        touches.Add(outcome.File!.Info);
                        break;
                    case ReadKind.Failed:
                        failed++;
                        break;
                    default:
                        break; // Vanished between listing and reading: handled as removed on the next scan.
                }
            }

            if (notes.Count > 0)
            {
                await _index.UpsertAsync(notes, cancellationToken).ConfigureAwait(false);
                indexed += notes.Count;
            }

            if (touches.Count > 0)
            {
                await _index.TouchAsync(touches, cancellationToken).ConfigureAwait(false);
                touched += touches.Count;
            }

            processed += chunk.Length;
            progress?.Report(new IndexProgress(processed, candidates.Count));
        }

        var removed = states.Keys.Where(path => !pathsOnDisk.Contains(path)).ToList();
        if (removed.Count > 0)
        {
            await _index.RemoveAsync(removed, cancellationToken).ConfigureAwait(false);
        }

        var summary = new IndexRunSummary(
            Scanned: onDisk.Count,
            Indexed: indexed,
            Unchanged: onDisk.Count - candidates.Count + touched,
            Removed: removed.Count,
            Failed: failed,
            Elapsed: stopwatch.Elapsed);
        LogScanCompleted(summary.Scanned, summary.Indexed, summary.Unchanged, summary.Removed, summary.Failed, summary.Elapsed.TotalMilliseconds);
        return summary;
    }

    /// <summary>Splits the files into batches of at most <paramref name="maxCount"/> notes and <see cref="MaxBatchBytes"/>.</summary>
    internal static IEnumerable<NoteFileInfo[]> Batch(IReadOnlyList<NoteFileInfo> files, int maxCount)
    {
        var batch = new List<NoteFileInfo>(Math.Min(maxCount, files.Count));
        long bytes = 0;
        foreach (var file in files)
        {
            if (batch.Count > 0 && (batch.Count == maxCount || bytes + file.Size > MaxBatchBytes))
            {
                yield return [.. batch];
                batch.Clear();
                bytes = 0;
            }

            batch.Add(file);
            bytes += file.Size;
        }

        if (batch.Count > 0)
        {
            yield return [.. batch];
        }
    }

    public async Task IndexFileAsync(NotePath path, CancellationToken cancellationToken)
    {
        var file = await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            await _index.RemoveAsync([path], cancellationToken).ConfigureAwait(false);
            return;
        }

        await IndexAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public async Task IndexAsync(NoteFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var path = file.Info.Path;
        var document = NoteDocumentParser.Parse(file.Text, path.FileNameWithoutExtension);
        var id = document.Metadata.Id ?? NoteId.FromPath(path);

        if (document.Metadata.Id is { } declared)
        {
            var owner = await _index.FindPathByIdAsync(declared, cancellationToken).ConfigureAwait(false);
            if (owner is { } ownerPath
                && ownerPath != path
                && await _files.GetInfoAsync(ownerPath, cancellationToken).ConfigureAwait(false) is not null)
            {
                LogDuplicateId(path.Value, ownerPath.Value);
                id = NoteId.FromPath(path);
            }
        }

        await _index.UpsertAsync([ToIndexedNote(file, document, id)], cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(IReadOnlyCollection<NotePath> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Count == 0 ? Task.CompletedTask : _index.RemoveAsync(paths, cancellationToken);
    }

    private async Task<ReadOutcome[]> ReadChunkAsync(
        NoteFileInfo[] chunk,
        IReadOnlyDictionary<NotePath, IndexedFileState> states,
        CancellationToken cancellationToken)
    {
        var outcomes = new ReadOutcome[chunk.Length];

        // Each file writes to its own slot, so results keep the (sorted) order of the chunk.
        await Parallel.ForAsync(
            0,
            chunk.Length,
            new ParallelOptions { MaxDegreeOfParallelism = _options.EffectiveParallelism, CancellationToken = cancellationToken },
            async (i, token) => outcomes[i] = await ReadOneAsync(chunk[i], states, token).ConfigureAwait(false)).ConfigureAwait(false);

        return outcomes;
    }

    private async Task<ReadOutcome> ReadOneAsync(
        NoteFileInfo info,
        IReadOnlyDictionary<NotePath, IndexedFileState> states,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await _files.ReadAsync(info.Path, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                return new ReadOutcome(ReadKind.Missing, null, null);
            }

            if (states.TryGetValue(info.Path, out var state) && state.Hash == file.Hash)
            {
                return new ReadOutcome(ReadKind.Touched, file, null);
            }

            var document = NoteDocumentParser.Parse(file.Text, info.Path.FileNameWithoutExtension);
            return new ReadOutcome(ReadKind.Changed, file, document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // One unreadable file must not abort the scan of the whole vault.
            LogFileFailed(exception, info.Path.Value);
            return new ReadOutcome(ReadKind.Failed, null, null);
        }
    }

    private IndexedNote ResolveIdentity(
        NoteFile file,
        NoteDocument document,
        IReadOnlyDictionary<NotePath, IndexedFileState> states,
        Dictionary<NoteId, NotePath> owners)
    {
        var path = file.Info.Path;
        var id = document.Metadata.Id ?? NoteId.FromPath(path);

        // The file stops owning whatever id it had before this change.
        if (states.TryGetValue(path, out var previous)
            && previous.Id != id
            && owners.TryGetValue(previous.Id, out var previousOwner)
            && previousOwner == path)
        {
            owners.Remove(previous.Id);
        }

        if (owners.TryGetValue(id, out var owner) && owner != path)
        {
            // Another existing file already uses this id (typically a copied note).
            LogDuplicateId(path.Value, owner.Value);
            id = NoteId.FromPath(path);
        }

        owners[id] = path;
        return ToIndexedNote(file, document, id);
    }

    private static IndexedNote ToIndexedNote(NoteFile file, NoteDocument document, NoteId id) =>
        new(id, file.Info.Path, document.Metadata, document.Body, file.Hash, file.Info.Size, file.Info.LastWriteTimeUtc);

    private enum ReadKind
    {
        Missing,
        Touched,
        Changed,
        Failed,
    }

    private readonly record struct ReadOutcome(ReadKind Kind, NoteFile? File, NoteDocument? Document);

    // Log messages carry paths and counters only, never note content.
    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Vault scan completed: {Scanned} files, {Indexed} indexed, {Unchanged} unchanged, {Removed} removed, {Failed} failed in {ElapsedMs:F0} ms")]
    private partial void LogScanCompleted(int scanned, int indexed, int unchanged, int removed, int failed, double elapsedMs);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Could not read note '{Path}'; it was skipped")]
    private partial void LogFileFailed(Exception exception, string path);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning,
        Message = "Note '{Path}' declares the same id as '{OwnerPath}'; a path-derived id is used until it is saved")]
    private partial void LogDuplicateId(string path, string ownerPath);
}
