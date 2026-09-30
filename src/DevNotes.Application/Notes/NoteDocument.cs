using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

public enum FrontmatterStatus
{
    /// <summary>The note has no frontmatter block.</summary>
    None,

    Valid,

    /// <summary>A block delimited by <c>---</c> exists but it is not a YAML mapping; it is left untouched.</summary>
    Invalid,
}

/// <summary>Result of parsing the text of a note file.</summary>
/// <param name="Metadata">Frontmatter values with fallbacks applied (the title is never empty).</param>
/// <param name="Body">Markdown content without the frontmatter block.</param>
/// <param name="BodyOffset">Offset of <paramref name="Body"/> inside the original text.</param>
/// <param name="FrontmatterStatus">Whether a usable frontmatter block was found.</param>
/// <param name="FrontmatterError">Human-readable reason when the status is <see cref="FrontmatterStatus.Invalid"/>.</param>
public sealed record NoteDocument(
    NoteMetadata Metadata,
    string Body,
    int BodyOffset,
    FrontmatterStatus FrontmatterStatus,
    string? FrontmatterError);
