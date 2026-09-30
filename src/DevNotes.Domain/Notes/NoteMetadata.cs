namespace DevNotes.Domain.Notes;

/// <summary>Metadata of a note as declared in its YAML frontmatter.</summary>
public sealed record NoteMetadata
{
    /// <summary>Identifier from the frontmatter; null when the note does not declare one yet.</summary>
    public NoteId? Id { get; init; }

    public required string Title { get; init; }

    public string? Project { get; init; }

    public IReadOnlyList<Tag> Tags { get; init; } = [];

    public NoteType Type { get; init; } = NoteType.Note;

    public DateOnly? Created { get; init; }

    public DateOnly? Updated { get; init; }

    public NoteLinks Links { get; init; } = NoteLinks.Empty;
}
