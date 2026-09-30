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

public sealed record SearchHit(NoteSummary Note, IReadOnlyList<SnippetSegment> Snippet, double Score);

public sealed record NoteListQuery(NoteSortOrder Sort, int Limit);

public sealed record SearchQuery(FtsQuery Query, NoteSortOrder Sort, int Limit);

/// <summary>One row of the note list: either a plain listing entry or a search hit with highlights.</summary>
public sealed record NoteListEntry(NoteSummary Note, IReadOnlyList<SnippetSegment> Snippet);

public sealed record NoteQueryResult(IReadOnlyList<NoteListEntry> Entries, bool IsSearch, bool IsTruncated);
