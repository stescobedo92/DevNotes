using DevNotes.Domain.Notes;

namespace DevNotes.Application.Abstractions;

/// <summary>Size and modification time of a note file; the cheap change signal used by incremental indexing.</summary>
public readonly record struct NoteFileInfo(NotePath Path, long Size, DateTimeOffset LastWriteTimeUtc);

/// <summary>A note file read from disk together with the hash of its exact bytes.</summary>
public sealed record NoteFile(NoteFileInfo Info, string Text, ContentHash Hash);

/// <summary>A note that was deleted from the vault and can still be restored.</summary>
public sealed record TrashEntry(string Id, NotePath OriginalPath, DateTimeOffset DeletedAtUtc);

public enum NoteWriteMode
{
    /// <summary>Fails with <see cref="NoteAlreadyExistsException"/> when the file already exists.</summary>
    CreateNew,

    /// <summary>Replaces the file atomically.</summary>
    Overwrite,
}

/// <summary>
/// File operations scoped to one vault. Implementations must never touch anything outside the
/// vault root and must write atomically (temporary file + rename).
/// </summary>
public interface INoteFileStore
{
    string RootPath { get; }

    /// <summary>All notes in the vault ordered by path. Hidden folders (.git, .devnotes, …) are skipped.</summary>
    Task<IReadOnlyList<NoteFileInfo>> ListNotesAsync(CancellationToken cancellationToken);

    /// <summary>Folders that can contain notes, relative to the vault root and ordered by path.</summary>
    Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken);

    /// <summary>Returns null when the file does not exist.</summary>
    Task<NoteFileInfo?> GetInfoAsync(NotePath path, CancellationToken cancellationToken);

    /// <summary>Returns null when the file does not exist.</summary>
    Task<NoteFile?> ReadAsync(NotePath path, CancellationToken cancellationToken);

    Task<NoteFile> WriteAsync(NotePath path, string text, NoteWriteMode mode, CancellationToken cancellationToken);

    /// <summary>Renames or moves a note. Never overwrites an existing destination.</summary>
    Task MoveAsync(NotePath source, NotePath destination, CancellationToken cancellationToken);

    Task<TrashEntry> MoveToTrashAsync(NotePath path, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken cancellationToken);

    /// <summary>Restores a trashed note and returns its path (a new name is chosen if the original one is taken).</summary>
    Task<NotePath> RestoreFromTrashAsync(string trashId, CancellationToken cancellationToken);

    Task DeleteFromTrashAsync(string trashId, CancellationToken cancellationToken);

    Task EmptyTrashAsync(CancellationToken cancellationToken);
}

public interface INoteFileStoreFactory
{
    INoteFileStore Create(string vaultRootPath);
}

public sealed class NoteAlreadyExistsException(NotePath path)
    : IOException($"A note already exists at '{path}'.")
{
    public NotePath Path { get; } = path;
}

public sealed class NoteNotFoundException(NotePath path)
    : IOException($"The note '{path}' does not exist.")
{
    public NotePath Path { get; } = path;
}

public sealed class TrashEntryNotFoundException(string trashId)
    : IOException($"The trash entry '{trashId}' does not exist.")
{
    public string TrashId { get; } = trashId;
}
