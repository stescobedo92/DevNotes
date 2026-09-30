namespace DevNotes.Domain.Notes;

public enum NoteType
{
    Note = 0,
    Bug,
    Adr,
    Runbook,
    Learning,
    Snippet,
}

public static class NoteTypes
{
    public static IReadOnlyList<NoteType> All { get; } =
    [
        NoteType.Note, NoteType.Bug, NoteType.Adr, NoteType.Runbook, NoteType.Learning, NoteType.Snippet,
    ];

    /// <summary>Value written to the <c>type</c> frontmatter key.</summary>
    public static string ToKey(this NoteType type) => type switch
    {
        NoteType.Note => "note",
        NoteType.Bug => "bug",
        NoteType.Adr => "adr",
        NoteType.Runbook => "runbook",
        NoteType.Learning => "learning",
        NoteType.Snippet => "snippet",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown note type."),
    };

    public static bool TryParse(string? text, out NoteType type)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "note":
                type = NoteType.Note;
                return true;
            case "bug":
                type = NoteType.Bug;
                return true;
            case "adr":
                type = NoteType.Adr;
                return true;
            case "runbook":
                type = NoteType.Runbook;
                return true;
            case "learning":
                type = NoteType.Learning;
                return true;
            case "snippet":
                type = NoteType.Snippet;
                return true;
            default:
                type = NoteType.Note;
                return false;
        }
    }

    /// <summary>Unknown or missing types fall back to <see cref="NoteType.Note"/> so foreign vaults still index.</summary>
    public static NoteType ParseOrDefault(string? text) => TryParse(text, out var type) ? type : NoteType.Note;
}
