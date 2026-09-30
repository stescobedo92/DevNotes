using System.Diagnostics.CodeAnalysis;
using DevNotes.Application.Abstractions;
using DevNotes.Application.Indexing;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Logging;

namespace DevNotes.Application.Notes;

/// <summary>A note loaded for editing. <see cref="Hash"/> identifies the on-disk version the text came from.</summary>
public sealed record OpenedNote(NotePath Path, string Text, ContentHash Hash, NoteDocument Document);

public sealed record NewNoteRequest(
    string Title,
    string? Folder = null,
    NoteType Type = NoteType.Note,
    string? Project = null);

public enum SaveMode
{
    /// <summary>Refuses to write when the file changed or disappeared since it was loaded.</summary>
    DetectConflicts,

    /// <summary>The user explicitly chose to keep their version.</summary>
    Overwrite,
}

public enum SaveConflictKind
{
    ModifiedOnDisk,
    DeletedOnDisk,
}

public abstract record SaveResult
{
    private SaveResult()
    {
    }

    /// <summary>The note is on disk. <see cref="Note"/> carries the text as written (the frontmatter may have been stamped).</summary>
    public sealed record Saved(OpenedNote Note) : SaveResult;

    /// <summary>Nothing was written because the file on disk is not the version the edit started from.</summary>
    public sealed record Conflict(SaveConflictKind Kind, NoteFile? Disk) : SaveResult;
}

public interface INoteService
{
    /// <summary>Returns null when the note does not exist.</summary>
    Task<OpenedNote?> OpenAsync(NotePath path, CancellationToken cancellationToken);

    /// <param name="path">Note to write.</param>
    /// <param name="text">Full text of the note.</param>
    /// <param name="baseHash">Hash of the on-disk version the edit started from.</param>
    /// <param name="mode">Whether a changed or missing file blocks the save.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<SaveResult> SaveAsync(NotePath path, string text, ContentHash baseHash, SaveMode mode, CancellationToken cancellationToken);

    Task<OpenedNote> CreateAsync(NewNoteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="text"/> as a new note next to <paramref name="source"/> with its own id.
    /// Used to keep the user's version when a conflict is resolved in favour of the file on disk.
    /// </summary>
    Task<OpenedNote> CreateCopyAsync(NotePath source, string text, CancellationToken cancellationToken);

    /// <summary>Changes the title of a note and renames its file to match. Returns the new path.</summary>
    Task<NotePath> RenameAsync(NotePath path, string newTitle, CancellationToken cancellationToken);

    /// <summary>Moves a note to another folder of the vault. Returns the new path.</summary>
    Task<NotePath> MoveAsync(NotePath path, string? targetFolder, CancellationToken cancellationToken);

    /// <summary>Sends the note to the vault trash, from where it can be restored.</summary>
    Task<TrashEntry> DeleteAsync(NotePath path, CancellationToken cancellationToken);

    Task<NotePath> RestoreAsync(string trashId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken cancellationToken);

    Task DeleteFromTrashAsync(string trashId, CancellationToken cancellationToken);

    Task EmptyTrashAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken);
}

public sealed partial class NoteService : INoteService
{
    private const int MaxNameAttempts = 100;

    private readonly INoteFileStore _files;
    private readonly IVaultIndexer _indexer;
    private readonly VaultEventHub _events;
    private readonly INoteIdGenerator _idGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public NoteService(
        INoteFileStore files,
        IVaultIndexer indexer,
        VaultEventHub events,
        INoteIdGenerator idGenerator,
        TimeProvider timeProvider,
        ILogger<NoteService> logger)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private DateOnly Today => DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);

    public async Task<OpenedNote?> OpenAsync(NotePath path, CancellationToken cancellationToken)
    {
        var file = await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return file is null ? null : ToOpenedNote(file);
    }

    public async Task<SaveResult> SaveAsync(NotePath path, string text, ContentHash baseHash, SaveMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        var disk = await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (mode == SaveMode.DetectConflicts)
        {
            if (disk is null)
            {
                return new SaveResult.Conflict(SaveConflictKind.DeletedOnDisk, null);
            }

            if (disk.Hash != baseHash)
            {
                return new SaveResult.Conflict(SaveConflictKind.ModifiedOnDisk, disk);
            }
        }

        if (disk is not null && string.Equals(disk.Text, text, StringComparison.Ordinal))
        {
            // Nothing changed: do not touch the file nor bump its `updated` date.
            return new SaveResult.Saved(ToOpenedNote(disk));
        }

        var fallbackTitle = path.FileNameWithoutExtension;
        var stamped = NoteStamper.Stamp(text, fallbackTitle, IdGeneratorFor(disk, fallbackTitle), Today);

        // The note goes back in the encoding it was read in. With conflict detection the store checks
        // once more, right before replacing the file, that it still is the version that was compared.
        var options = new NoteWriteOptions(
            disk?.Encoding ?? NoteTextEncoding.Utf8,
            mode == SaveMode.DetectConflicts ? disk?.Info : null);
        NoteFile written;
        try
        {
            written = await _files.WriteAsync(path, stamped.Text, NoteWriteMode.Overwrite, options, cancellationToken).ConfigureAwait(false);
        }
        catch (NoteChangedOnDiskException)
        {
            // Another program saved the note while this save was being written: nothing was replaced.
            var current = await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return new SaveResult.Conflict(current is null ? SaveConflictKind.DeletedOnDisk : SaveConflictKind.ModifiedOnDisk, current);
        }

        await IndexAfterWriteAsync(written, cancellationToken).ConfigureAwait(false);
        _events.PublishNotesChanged(NotesChangeSource.Local, path);
        return new SaveResult.Saved(new OpenedNote(path, written.Text, written.Hash, stamped.Document));
    }

    public async Task<OpenedNote> CreateAsync(NewNoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = RequireTitle(request.Title, nameof(request));
        var slug = Slug.From(title);

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var path = BuildPath(request.Folder, Numbered(slug, attempt), nameof(request));
            var content = NoteTemplates.CreateDefault(_idGenerator.NewId(), title, request.Type, request.Project, Today);
            try
            {
                var written = await _files.WriteAsync(path, content, NoteWriteMode.CreateNew, cancellationToken).ConfigureAwait(false);
                await IndexAfterWriteAsync(written, cancellationToken).ConfigureAwait(false);
                _events.PublishNotesChanged(NotesChangeSource.Local, path);
                return ToOpenedNote(written);
            }
            catch (NoteAlreadyExistsException)
            {
                // Name taken (possibly by a concurrent writer): try the next suffix.
            }
        }

        throw new IOException($"Could not find a free file name for '{slug}' after {MaxNameAttempts} attempts.");
    }

    public async Task<OpenedNote> CreateCopyAsync(NotePath source, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A copy must not share the id of the original, or the two notes would fight for one identity.
        var content = NoteStamper.AssignNewId(text, source.FileNameWithoutExtension, _idGenerator, Today).Text;
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var path = source.WithFileName(Numbered(source.FileNameWithoutExtension + "-copy", attempt));
            try
            {
                var written = await _files.WriteAsync(path, content, NoteWriteMode.CreateNew, cancellationToken).ConfigureAwait(false);
                await IndexAfterWriteAsync(written, cancellationToken).ConfigureAwait(false);
                _events.PublishNotesChanged(NotesChangeSource.Local, path);
                return ToOpenedNote(written);
            }
            catch (NoteAlreadyExistsException)
            {
                // Try the next suffix.
            }
        }

        throw new IOException($"Could not find a free file name for a copy of '{source}' after {MaxNameAttempts} attempts.");
    }

    public async Task<NotePath> RenameAsync(NotePath path, string newTitle, CancellationToken cancellationToken)
    {
        var title = RequireTitle(newTitle, nameof(newTitle));
        var file = await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new NoteNotFoundException(path);

        var fallbackTitle = path.FileNameWithoutExtension;
        var retitled = NoteStamper.SetTitle(file.Text, fallbackTitle, title);
        var stamped = NoteStamper.Stamp(retitled.Text, fallbackTitle, _idGenerator, Today);
        if (!string.Equals(stamped.Text, file.Text, StringComparison.Ordinal))
        {
            // Same guarantees as a save: keep the encoding and never replace a version that was not read.
            var options = new NoteWriteOptions(file.Encoding, file.Info);
            await _files.WriteAsync(path, stamped.Text, NoteWriteMode.Overwrite, options, cancellationToken).ConfigureAwait(false);
        }

        var slug = Slug.From(title);
        var destination = path;
        if (!string.Equals(path.FileNameWithoutExtension, slug, StringComparison.Ordinal))
        {
            destination = await MoveToFreeNameAsync(path, slug, cancellationToken).ConfigureAwait(false);
        }

        await ReindexMovedAsync(path, destination, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public async Task<NotePath> MoveAsync(NotePath path, string? targetFolder, CancellationToken cancellationToken)
    {
        var destination = BuildPath(targetFolder, path.FileNameWithoutExtension, nameof(targetFolder));
        if (destination == path)
        {
            return path;
        }

        await _files.MoveAsync(path, destination, cancellationToken).ConfigureAwait(false);
        await ReindexMovedAsync(path, destination, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public async Task<TrashEntry> DeleteAsync(NotePath path, CancellationToken cancellationToken)
    {
        var entry = await _files.MoveToTrashAsync(path, cancellationToken).ConfigureAwait(false);
        await RunIndexUpdateAsync(() => _indexer.RemoveAsync([path], cancellationToken), path).ConfigureAwait(false);
        _events.PublishNotesChanged(NotesChangeSource.Local, path);
        return entry;
    }

    public async Task<NotePath> RestoreAsync(string trashId, CancellationToken cancellationToken)
    {
        var path = await _files.RestoreFromTrashAsync(trashId, cancellationToken).ConfigureAwait(false);
        await RunIndexUpdateAsync(() => _indexer.IndexFileAsync(path, cancellationToken), path).ConfigureAwait(false);
        _events.PublishNotesChanged(NotesChangeSource.Local, path);
        return path;
    }

    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken cancellationToken) =>
        _files.ListTrashAsync(cancellationToken);

    public Task DeleteFromTrashAsync(string trashId, CancellationToken cancellationToken) =>
        _files.DeleteFromTrashAsync(trashId, cancellationToken);

    public Task EmptyTrashAsync(CancellationToken cancellationToken) => _files.EmptyTrashAsync(cancellationToken);

    public Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken) =>
        _files.ListFoldersAsync(cancellationToken);

    /// <summary>
    /// A note keeps its identity: when the text being saved has lost its <c>id</c> (an undo past the
    /// moment it was stamped, a line deleted by mistake) the id still on disk is written again instead
    /// of minting a new one, which would orphan every reference to the note.
    /// </summary>
    private INoteIdGenerator IdGeneratorFor(NoteFile? disk, string fallbackTitle) =>
        disk is not null && NoteDocumentParser.Parse(disk.Text, fallbackTitle).Metadata.Id is { } existing
            ? new FixedNoteIdGenerator(existing)
            : _idGenerator;

    private static OpenedNote ToOpenedNote(NoteFile file) =>
        new(file.Info.Path, file.Text, file.Hash, NoteDocumentParser.Parse(file.Text, file.Info.Path.FileNameWithoutExtension));

    private static string Numbered(string slug, int attempt) => attempt == 1 ? slug : $"{slug}-{attempt}";

    private static string RequireTitle(string? title, string parameterName) =>
        NoteTitle.TryNormalize(title, out var normalized)
            ? normalized
            : throw new ArgumentException($"A title must have between 1 and {NoteTitle.MaxLength} characters.", parameterName);

    private static NotePath BuildPath(string? folder, string fileName, string parameterName) =>
        NotePath.TryCombine(folder, fileName, out var path, out var error)
            ? path
            : throw new ArgumentException($"Invalid location: {error}.", parameterName);

    private async Task<NotePath> MoveToFreeNameAsync(NotePath source, string slug, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var candidate = source.WithFileName(Numbered(slug, attempt));
            if (candidate == source)
            {
                return source;
            }

            try
            {
                await _files.MoveAsync(source, candidate, cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (NoteAlreadyExistsException)
            {
                // Try the next suffix.
            }
        }

        throw new IOException($"Could not find a free file name for '{slug}' after {MaxNameAttempts} attempts.");
    }

    private async Task ReindexMovedAsync(NotePath source, NotePath destination, CancellationToken cancellationToken)
    {
        await RunIndexUpdateAsync(
            async () =>
            {
                // Index the destination first so a note with a stable id keeps its row; then drop the old path.
                await _indexer.IndexFileAsync(destination, cancellationToken).ConfigureAwait(false);
                if (destination != source)
                {
                    await _indexer.RemoveAsync([source], cancellationToken).ConfigureAwait(false);
                }
            },
            destination).ConfigureAwait(false);

        _events.PublishNotesChanged(NotesChangeSource.Local, source == destination ? [source] : [source, destination]);
    }

    private Task IndexAfterWriteAsync(NoteFile written, CancellationToken cancellationToken) =>
        RunIndexUpdateAsync(() => _indexer.IndexAsync(written, cancellationToken), written.Info.Path);

    /// <summary>
    /// The file operation already succeeded, so a failure to update the (disposable) index must not
    /// be reported as a failed save. It is logged and surfaced through the index status instead; the
    /// next scan repairs the index.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Index failures are reported through the index status; the note itself is safely on disk.")]
    private async Task RunIndexUpdateAsync(Func<Task> update, NotePath path)
    {
        try
        {
            await update().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogIndexUpdateFailed(exception, path.Value);
            _events.PublishStatus(new IndexStatus(IndexState.Failed, default, exception.Message));
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "The note '{Path}' was written but the index could not be updated")]
    private partial void LogIndexUpdateFailed(Exception exception, string path);
}
