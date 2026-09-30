using DevNotes.Domain.Notes;

namespace DevNotes.Application.Abstractions;

/// <summary>Size and modification time of a note file; the cheap change signal used by incremental indexing.</summary>
public readonly record struct NoteFileInfo(NotePath Path, long Size, DateTimeOffset LastWriteTimeUtc);

/// <summary>How the bytes of a note file map to its text. A note is written back the way it was read.</summary>
public enum NoteTextEncoding
{
    /// <summary>UTF-8 without a byte-order mark: the encoding of every note created by the app.</summary>
    Utf8 = 0,

    /// <summary>UTF-8 starting with a byte-order mark.</summary>
    Utf8WithBom,

    /// <summary>UTF-16 little endian with a byte-order mark (e.g. Windows PowerShell redirection).</summary>
    Utf16LittleEndian,

    /// <summary>UTF-16 big endian with a byte-order mark.</summary>
    Utf16BigEndian,

    /// <summary>
    /// Not valid Unicode: read as a single-byte code page (Windows-1252) in which every byte is a
    /// character and back, so the bytes that are not edited are preserved exactly.
    /// </summary>
    Legacy,
}

/// <summary>A note file read from disk together with the hash of its exact bytes.</summary>
public sealed record NoteFile(NoteFileInfo Info, string Text, ContentHash Hash, NoteTextEncoding Encoding = NoteTextEncoding.Utf8);

/// <param name="Encoding">
/// Encoding to write with. Text that cannot be represented in it (a character outside the legacy
/// code page) is written as UTF-8 instead, so nothing typed is ever lost.
/// </param>
/// <param name="ExpectedOnDisk">
/// When set, the file is only replaced if it still is this version; otherwise the write fails with
/// <see cref="NoteChangedOnDiskException"/> and the file is left untouched.
/// </param>
public readonly record struct NoteWriteOptions(NoteTextEncoding Encoding = NoteTextEncoding.Utf8, NoteFileInfo? ExpectedOnDisk = null);

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

    Task<NoteFile> WriteAsync(NotePath path, string text, NoteWriteMode mode, NoteWriteOptions options, CancellationToken cancellationToken);

    /// <summary>Renames or moves a note. Never overwrites an existing destination.</summary>
    Task MoveAsync(NotePath source, NotePath destination, CancellationToken cancellationToken);

    Task<TrashEntry> MoveToTrashAsync(NotePath path, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken cancellationToken);

    /// <summary>Restores a trashed note and returns its path (a new name is chosen if the original one is taken).</summary>
    Task<NotePath> RestoreFromTrashAsync(string trashId, CancellationToken cancellationToken);

    Task DeleteFromTrashAsync(string trashId, CancellationToken cancellationToken);

    Task EmptyTrashAsync(CancellationToken cancellationToken);
}

public static class NoteFileStoreExtensions
{
    /// <summary>Writes a note as UTF-8 without any precondition on the file being replaced.</summary>
    public static Task<NoteFile> WriteAsync(this INoteFileStore store, NotePath path, string text, NoteWriteMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.WriteAsync(path, text, mode, default, cancellationToken);
    }
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

/// <summary>The file was modified or removed by another program between the conflict check and the write.</summary>
public sealed class NoteChangedOnDiskException(NotePath path)
    : IOException($"The note '{path}' changed on disk while it was being saved.")
{
    public NotePath Path { get; } = path;
}

public sealed class TrashEntryNotFoundException(string trashId)
    : IOException($"The trash entry '{trashId}' does not exist.")
{
    public string TrashId { get; } = trashId;
}
