namespace DevNotes.Domain.Notes;

public enum NoteLinkKind
{
    Commit,
    Ticket,
    Note,
}

public static class NoteLinkKinds
{
    /// <summary>Value stored in the <c>links.kind</c> column of the index.</summary>
    public static string ToKey(this NoteLinkKind kind) => kind switch
    {
        NoteLinkKind.Commit => "commit",
        NoteLinkKind.Ticket => "ticket",
        NoteLinkKind.Note => "note",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown link kind."),
    };
}

/// <summary>Outgoing references declared in the frontmatter of a note.</summary>
public sealed record NoteLinks(
    IReadOnlyList<string> Commits,
    IReadOnlyList<string> Tickets,
    IReadOnlyList<string> Notes)
{
    public static NoteLinks Empty { get; } = new([], [], []);

    public bool IsEmpty => Commits.Count == 0 && Tickets.Count == 0 && Notes.Count == 0;

    /// <summary>All links flattened as (kind, target) pairs.</summary>
    public IEnumerable<(NoteLinkKind Kind, string Target)> Enumerate()
    {
        foreach (var commit in Commits)
        {
            yield return (NoteLinkKind.Commit, commit);
        }

        foreach (var ticket in Tickets)
        {
            yield return (NoteLinkKind.Ticket, ticket);
        }

        foreach (var note in Notes)
        {
            yield return (NoteLinkKind.Note, note);
        }
    }

    /// <summary>Removes the wiki-link brackets from a note reference: <c>[[target]]</c> → <c>target</c>.</summary>
    public static string NormalizeNoteTarget(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var span = reference.AsSpan().Trim();
        if (span.StartsWith("[[", StringComparison.Ordinal) && span.EndsWith("]]", StringComparison.Ordinal) && span.Length >= 4)
        {
            span = span[2..^2].Trim();
        }

        // `[[target|alias]]` keeps only the target.
        var pipe = span.IndexOf('|');
        return (pipe >= 0 ? span[..pipe].TrimEnd() : span).ToString();
    }
}
