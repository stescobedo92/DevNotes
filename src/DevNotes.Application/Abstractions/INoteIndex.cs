using DevNotes.Application.Search;
using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;

namespace DevNotes.Application.Abstractions;

/// <summary>Everything the index stores about one note file.</summary>
public sealed record IndexedNote(
    NoteId Id,
    NotePath Path,
    NoteMetadata Metadata,
    string Body,
    ContentHash Hash,
    long FileSize,
    DateTimeOffset FileLastWriteUtc);

/// <summary>What the index remembers about a file, used to skip unchanged files on the next scan.</summary>
public readonly record struct IndexedFileState(
    NoteId Id,
    ContentHash Hash,
    long FileSize,
    DateTimeOffset FileLastWriteUtc);

/// <summary>
/// The disposable search index of one vault. It can be deleted at any time and rebuilt from
/// the Markdown files, which remain the source of truth.
/// </summary>
public interface INoteIndex : IAsyncDisposable
{
    /// <summary>Creates or migrates the schema. Must be called once before any other member.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<NotePath, IndexedFileState>> GetFileStatesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or updates the notes in one transaction, following a note when its path or id changed.</summary>
    Task UpsertAsync(IReadOnlyCollection<IndexedNote> notes, CancellationToken cancellationToken);

    /// <summary>Records a new size / modification time for a file whose content did not change.</summary>
    Task TouchAsync(IReadOnlyCollection<NoteFileInfo> files, CancellationToken cancellationToken);

    Task RemoveAsync(IReadOnlyCollection<NotePath> paths, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);

    Task<NotePath?> FindPathByIdAsync(NoteId id, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<NoteSummary>> ListAsync(NoteListQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}

public interface INoteIndexFactory
{
    /// <summary>Opens (without initializing) the index that belongs to the vault.</summary>
    INoteIndex Open(VaultId vaultId);

    /// <summary>Deletes the index files of a vault that is no longer registered.</summary>
    Task DeleteAsync(VaultId vaultId, CancellationToken cancellationToken);
}
