using DevNotes.Domain.Notes;

namespace DevNotes.Application.Search;

public enum NoteSortOrder
{
    /// <summary>Most recently updated first.</summary>
    UpdatedDescending = 0,

    TitleAscending,

    /// <summary>Best full-text match first. Falls back to <see cref="UpdatedDescending"/> without a search text.</summary>
    Relevance,
}

/// <summary>Lightweight projection of an indexed note, enough to render a list row.</summary>
public sealed record NoteSummary(
    NoteId Id,
    NotePath Path,
    string Title,
    string? Project,
    NoteType Type,
    IReadOnlyList<string> Tags,
    DateOnly? Created,
    DateOnly? Updated,
    DateTimeOffset FileLastWriteUtc,
    string Excerpt);

/// <summary>A piece of a result fragment; matched terms are flagged so the UI can highlight them.</summary>
public readonly record struct SnippetSegment(string Text, bool IsMatch);

/// <param name="Note">The matching note.</param>
/// <param name="Title">Title split into plain and matched segments.</param>
/// <param name="Snippet">Body fragment around the best match, split into plain and matched segments.</param>
/// <param name="Score">BM25 rank as returned by FTS5 (lower is better).</param>
public sealed record SearchHit(
    NoteSummary Note,
    IReadOnlyList<SnippetSegment> Title,
    IReadOnlyList<SnippetSegment> Snippet,
    double Score);

public sealed record NoteListQuery(NoteSortOrder Sort, int Limit);

/// <summary>
/// A search of the index. <see cref="Query"/> may be empty: the notes are then only restricted by
/// <see cref="Filter"/> and by the exclusions of the query, and returned in the requested order
/// with a plain excerpt instead of a highlighted fragment.
/// </summary>
public sealed record SearchQuery(FtsQuery Query, NoteSortOrder Sort, int Limit)
{
    public NoteFilter Filter { get; init; } = NoteFilter.Empty;
}

/// <summary>How many notes carry one value of a field.</summary>
public sealed record FacetCount(string Value, int Count);

/// <summary>Distinct projects, tags and types of the vault with the number of notes of each.</summary>
public sealed record NoteFacets(IReadOnlyList<FacetCount> Projects, IReadOnlyList<FacetCount> Tags, IReadOnlyList<FacetCount> Types)
{
    public static NoteFacets Empty { get; } = new([], [], []);
}

/// <summary>One row of the note list: either a plain listing entry or a search hit with highlights.</summary>
public sealed record NoteListEntry(
    NoteSummary Note,
    IReadOnlyList<SnippetSegment> Title,
    IReadOnlyList<SnippetSegment> Snippet);

public sealed record NoteQueryResult(IReadOnlyList<NoteListEntry> Entries, bool IsSearch, bool IsTruncated);
