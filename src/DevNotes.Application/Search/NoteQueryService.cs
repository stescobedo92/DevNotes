using DevNotes.Application.Abstractions;

namespace DevNotes.Application.Search;

public interface INoteQueryService
{
    /// <summary>
    /// Lists the notes of the vault, or searches them when <paramref name="searchText"/> contains
    /// at least one usable term.
    /// </summary>
    Task<NoteQueryResult> QueryAsync(string? searchText, NoteSortOrder sort, int limit, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);
}

public sealed class NoteQueryService(INoteIndex index) : INoteQueryService
{
    public const int DefaultLimit = 2_000;
    public const int MaxLimit = 20_000;
    private const int ExcerptLength = 200;

    private readonly INoteIndex _index = index ?? throw new ArgumentNullException(nameof(index));

    public async Task<NoteQueryResult> QueryAsync(string? searchText, NoteSortOrder sort, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        // One extra row tells whether the result was cut without a second COUNT query.
        var fetch = limit + 1;
        var fts = FtsQueryBuilder.Build(searchText);
        List<NoteListEntry> entries;

        if (fts.IsEmpty)
        {
            var effectiveSort = sort == NoteSortOrder.Relevance ? NoteSortOrder.UpdatedDescending : sort;
            var notes = await _index.ListAsync(new NoteListQuery(effectiveSort, fetch), cancellationToken).ConfigureAwait(false);
            entries = new List<NoteListEntry>(notes.Count);
            foreach (var note in notes)
            {
                var excerpt = SnippetParser.ToSingleLine(note.Excerpt, ExcerptLength);
                entries.Add(new NoteListEntry(note, excerpt.Length == 0 ? [] : [new SnippetSegment(excerpt, IsMatch: false)]));
            }
        }
        else
        {
            var hits = await _index.SearchAsync(new SearchQuery(fts, sort, fetch), cancellationToken).ConfigureAwait(false);
            entries = new List<NoteListEntry>(hits.Count);
            foreach (var hit in hits)
            {
                entries.Add(new NoteListEntry(hit.Note, hit.Snippet));
            }
        }

        var truncated = entries.Count > limit;
        if (truncated)
        {
            entries.RemoveRange(limit, entries.Count - limit);
        }

        return new NoteQueryResult(entries, IsSearch: !fts.IsEmpty, truncated);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken) => _index.CountAsync(cancellationToken);
}
