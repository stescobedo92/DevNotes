using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

/// <summary>Text to write plus how it parses.</summary>
public sealed record StampedNote(string Text, NoteDocument Document);

/// <summary>
/// Applies the metadata the app is responsible for right before a note is written:
/// a stable <c>id</c> (generated when missing) and the <c>updated</c> date.
/// </summary>
public static class NoteStamper
{
    public static StampedNote Stamp(string text, string fallbackTitle, INoteIdGenerator idGenerator, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);

        var document = NoteDocumentParser.Parse(text, fallbackTitle);
        if (document.FrontmatterStatus == FrontmatterStatus.Invalid)
        {
            // Never rewrite a block we could not understand.
            return new StampedNote(text, document);
        }

        var assignments = new List<FrontmatterAssignment>(2);
        if (document.Metadata.Id is null)
        {
            assignments.Add(new FrontmatterAssignment("id", YamlScalar.Format(idGenerator.NewId().Value), FrontmatterPlacement.Start));
        }

        if (document.Metadata.Updated != today)
        {
            assignments.Add(new FrontmatterAssignment("updated", YamlScalar.FormatDate(today)));
        }

        return Apply(text, fallbackTitle, document, assignments);
    }

    /// <summary>Sets the <c>title</c> key, keeping everything else untouched.</summary>
    public static StampedNote SetTitle(string text, string fallbackTitle, string title)
    {
        var document = NoteDocumentParser.Parse(text, fallbackTitle);
        if (document.FrontmatterStatus == FrontmatterStatus.Invalid)
        {
            return new StampedNote(text, document);
        }

        return Apply(text, fallbackTitle, document, [new FrontmatterAssignment("title", YamlScalar.Format(title))]);
    }

    private static StampedNote Apply(string text, string fallbackTitle, NoteDocument original, List<FrontmatterAssignment> assignments)
    {
        if (assignments.Count == 0)
        {
            return new StampedNote(text, original);
        }

        var patchedText = FrontmatterEditor.SetScalars(text, assignments);
        var patched = NoteDocumentParser.Parse(patchedText, fallbackTitle);

        // The textual edit is verified by parsing its result. If the frontmatter uses a construct the
        // editor does not understand and the patch broke it, the user's text wins and nothing is changed.
        var bodyPreserved = string.Equals(patched.Body.TrimStart('\r', '\n'), original.Body.TrimStart('\r', '\n'), StringComparison.Ordinal);
        return patched.FrontmatterStatus == FrontmatterStatus.Valid && bodyPreserved
            ? new StampedNote(patchedText, patched)
            : new StampedNote(text, original);
    }
}
