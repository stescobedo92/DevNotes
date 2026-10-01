using DevNotes.Application.Abstractions;
using DevNotes.Application.Search;
using DevNotes.Domain.Common;
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
            IReadOnlyList<NoteSummary> result = [.. Order(_notes, query.Sort).Take(query.Limit).Select(ToSummary)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        // The fake only needs to prove that the query reached the index: matching is a plain "contains".
        var needle = Needle(query.Query.Match);
        var excluded = query.Query.Exclude?.Split(" OR ").Select(term => Needle(term)!).ToList() ?? [];
        lock (_gate)
        {
            var matching = _notes
                .Where(note => needle is null || Contains(note, needle))
                .Where(note => !excluded.Any(term => Contains(note, term)))
                .Where(note => Matches(note, query.Filter));
            IReadOnlyList<SearchHit> hits = [.. Order(matching, query.Sort)
                .Take(query.Limit)
                .Select(note => needle is null
                    ? new SearchHit(ToSummary(note), [], [], 0)
                    : new SearchHit(
                        ToSummary(note),
                        [new SnippetSegment(note.Metadata.Title, IsMatch: false)],
                        [new SnippetSegment(needle, IsMatch: true)],
                        Score: -1))];
            return Task.FromResult(hits);
        }
    }

    public Task<NoteFacets> GetFacetsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new NoteFacets(
                Count(_notes.Where(note => note.Metadata.Project is not null).Select(note => note.Metadata.Project!)),
                Count(_notes.SelectMany(note => note.Metadata.Tags).Select(tag => tag.Value)),
                Count(_notes.Select(note => note.Metadata.Type.ToKey()))));
        }
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<FacetCount> Count(IEnumerable<string> values) =>
        [.. values
            .GroupBy(value => value, StringComparer.Ordinal)
            .Select(group => new FacetCount(group.Key, group.Count()))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Value, StringComparer.Ordinal)];

    private static IEnumerable<IndexedNote> Order(IEnumerable<IndexedNote> notes, NoteSortOrder sort) =>
        sort == NoteSortOrder.TitleAscending
            ? notes.OrderBy(note => note.Metadata.Title, StringComparer.OrdinalIgnoreCase)
            : notes.OrderByDescending(note => note.FileLastWriteUtc);

    private static string? Needle(string? match) =>
        match?.Replace("\"", string.Empty, StringComparison.Ordinal).TrimEnd('*').Trim();

    private static bool Contains(IndexedNote note, string needle) =>
        note.Body.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || note.Metadata.Title.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(IndexedNote note, NoteFilter filter)
    {
        var metadata = note.Metadata;
        if (filter.Projects.Count > 0
            && (metadata.Project is null || !filter.Projects.Any(project => TextKey.Of(project) == TextKey.Of(metadata.Project))))
        {
            return false;
        }

        if (filter.Tags.Any(tag => !metadata.Tags.Contains(tag)))
        {
            return false;
        }

        if (filter.Types.Count > 0 && !filter.Types.Contains(metadata.Type))
        {
            return false;
        }

        if (filter.Commits.Count > 0
            && !filter.Commits.Any(commit => metadata.Links.Commits.Any(known =>
                known.StartsWith(commit, StringComparison.OrdinalIgnoreCase) || commit.StartsWith(known, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (filter.Tickets.Count > 0 && !filter.Tickets.Any(ticket => metadata.Links.Tickets.Contains(ticket, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!filter.Created.IsEmpty && (metadata.Created is not { } created || !filter.Created.Contains(created)))
        {
            return false;
        }

        return filter.Updated.IsEmpty || (metadata.Updated is { } updated && filter.Updated.Contains(updated));
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
