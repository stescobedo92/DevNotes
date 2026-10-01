using DevNotes.Application.Abstractions;

namespace DevNotes.Application.Search;

public interface INoteQueryService
{
    /// <summary>
    /// Lists the notes of the vault, or searches them when <paramref name="searchText"/> contains
    /// at least one usable term, filter or exclusion. <paramref name="filter"/> (typically the
    /// selection of the sidebar) is combined with the filters typed in the text.
    /// </summary>
    Task<NoteQueryResult> QueryAsync(string? searchText, NoteFilter filter, NoteSortOrder sort, int limit, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<NoteFacets> GetFacetsAsync(CancellationToken cancellationToken);
}

public sealed class NoteQueryService(INoteIndex index) : INoteQueryService
{
    public const int DefaultLimit = 2_000;
    public const int MaxLimit = 20_000;
    private const int ExcerptLength = 200;

    private readonly INoteIndex _index = index ?? throw new ArgumentNullException(nameof(index));

    public async Task<NoteQueryResult> QueryAsync(string? searchText, NoteFilter filter, NoteSortOrder sort, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var parsed = SearchQueryParser.Parse(searchText);
        var combined = parsed.Filter.Merge(filter);
        if (combined.IsUnsatisfiable)
        {
            return new NoteQueryResult([], IsSearch: true, IsTruncated: false);
        }

        // One extra row tells whether the result was cut without a second COUNT query.
        var fetch = limit + 1;
        var fts = FtsQueryBuilder.Build(parsed);
        var isSearch = !fts.IsEmpty || fts.Exclude is not null || !combined.IsEmpty;
        List<NoteListEntry> entries;

        if (!isSearch)
        {
            var effectiveSort = sort == NoteSortOrder.Relevance ? NoteSortOrder.UpdatedDescending : sort;
            var notes = await _index.ListAsync(new NoteListQuery(effectiveSort, fetch), cancellationToken).ConfigureAwait(false);
            entries = new List<NoteListEntry>(notes.Count);
            foreach (var note in notes)
            {
                entries.Add(PlainEntry(note));
            }
        }
        else
        {
            var query = new SearchQuery(fts, sort, fetch) { Filter = combined };
            var hits = await _index.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            entries = new List<NoteListEntry>(hits.Count);
            foreach (var hit in hits)
            {
                // Without text to highlight the index returns no fragment: the row shows the excerpt.
                entries.Add(fts.IsEmpty ? PlainEntry(hit.Note) : new NoteListEntry(hit.Note, hit.Title, hit.Snippet));
            }
        }

        var truncated = entries.Count > limit;
        if (truncated)
        {
            entries.RemoveRange(limit, entries.Count - limit);
        }

        return new NoteQueryResult(entries, isSearch, truncated);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken) => _index.CountAsync(cancellationToken);

    public Task<NoteFacets> GetFacetsAsync(CancellationToken cancellationToken) => _index.GetFacetsAsync(cancellationToken);

    private static NoteListEntry PlainEntry(NoteSummary note)
    {
        var excerpt = SnippetParser.ToSingleLine(note.Excerpt, ExcerptLength);
        return new NoteListEntry(
            note,
            [new SnippetSegment(note.Title, IsMatch: false)],
            excerpt.Length == 0 ? [] : [new SnippetSegment(excerpt, IsMatch: false)]);
    }
}
