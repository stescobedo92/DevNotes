using DevNotes.Application.Abstractions;
using DevNotes.Application.Search;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Fakes;

/// <summary>In-memory index with the same identity rules as the SQLite implementation (unique id and unique path).</summary>
public sealed class InMemoryNoteIndex : INoteIndex
{
    private readonly Lock _gate = new();
    private readonly List<IndexedNote> _notes = [];

    public bool IsInitialized { get; private set; }

    public bool IsDisposed { get; private set; }

    public int UpsertCalls { get; private set; }

    public int UpsertedNotes { get; private set; }

    public int TouchedFiles { get; private set; }

    public int ClearCalls { get; private set; }

    /// <summary>When set, the next mutation throws (to test failure handling).</summary>
    public Exception? FailNextMutation { get; set; }

    public IReadOnlyList<IndexedNote> Notes
    {
        get
        {
            lock (_gate)
            {
                return [.. _notes];
            }
        }
    }

    public IndexedNote? Find(string path)
    {
        var notePath = NotePath.Create(path);
        lock (_gate)
        {
            return _notes.FirstOrDefault(note => note.Path == notePath);
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsInitialized = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<NotePath, IndexedFileState>> GetFileStatesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<NotePath, IndexedFileState> states = _notes.ToDictionary(
                note => note.Path,
                note => new IndexedFileState(note.Id, note.Hash, note.FileSize, note.FileLastWriteUtc));
            return Task.FromResult(states);
        }
    }

    public Task UpsertAsync(IReadOnlyCollection<IndexedNote> notes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            UpsertCalls++;
            UpsertedNotes += notes.Count;
            foreach (var note in notes)
            {
                _notes.RemoveAll(existing => existing.Path == note.Path || existing.Id == note.Id);
                _notes.Add(note);
            }
        }

        return Task.CompletedTask;
    }

    public Task TouchAsync(IReadOnlyCollection<NoteFileInfo> files, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            foreach (var file in files)
            {
                var index = _notes.FindIndex(note => note.Path == file.Path);
                if (index >= 0)
                {
                    _notes[index] = _notes[index] with { FileSize = file.Size, FileLastWriteUtc = file.LastWriteTimeUtc };
                    TouchedFiles++;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(IReadOnlyCollection<NotePath> paths, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            _notes.RemoveAll(note => paths.Contains(note.Path));
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ClearCalls++;
            _notes.Clear();
        }

        return Task.CompletedTask;
    }

    public Task<NotePath?> FindPathByIdAsync(NoteId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var match = _notes.FirstOrDefault(note => note.Id == id);
            return Task.FromResult<NotePath?>(match?.Path);
        }
    }

    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_notes.Count);
        }
    }

    public Task<IReadOnlyList<NoteSummary>> ListAsync(NoteListQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IEnumerable<IndexedNote> ordered = query.Sort == NoteSortOrder.TitleAscending
                ? _notes.OrderBy(note => note.Metadata.Title, StringComparer.OrdinalIgnoreCase)
                : _notes.OrderByDescending(note => note.FileLastWriteUtc);
            IReadOnlyList<NoteSummary> result = [.. ordered.Take(query.Limit).Select(ToSummary)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        // The fake only needs to prove that the query reached the index; matching is a plain "contains".
        var needle = (query.Query.Match ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).TrimEnd('*');
        lock (_gate)
        {
            IReadOnlyList<SearchHit> hits = [.. _notes
                .Where(note => note.Body.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || note.Metadata.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(query.Limit)
                .Select(note => new SearchHit(ToSummary(note), [new SnippetSegment(needle, IsMatch: true)], Score: -1))];
            return Task.FromResult(hits);
        }
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    private static NoteSummary ToSummary(IndexedNote note) => new(
        note.Id,
        note.Path,
        note.Metadata.Title,
        note.Metadata.Project,
        note.Metadata.Type,
        [.. note.Metadata.Tags.Select(tag => tag.Value)],
        note.Metadata.Created,
        note.Metadata.Updated,
        note.FileLastWriteUtc,
        note.Body.Length > 200 ? note.Body[..200] : note.Body);

    private void ThrowIfFailing()
    {
        if (FailNextMutation is { } failure)
        {
            FailNextMutation = null;
            throw failure;
        }
    }
}
