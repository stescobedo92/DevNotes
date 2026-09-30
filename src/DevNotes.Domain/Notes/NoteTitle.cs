using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DevNotes.Domain.Notes;

public static class NoteTitle
{
    public const int MaxLength = 200;

    /// <summary>
    /// Produces a single-line title: control characters removed, whitespace runs collapsed, trimmed.
    /// Returns false when nothing is left or the result is too long.
    /// </summary>
    public static bool TryNormalize(string? text, [NotNullWhen(true)] out string? title)
    {
        title = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        if (builder.Length is 0 or > MaxLength)
        {
            return false;
        }

        title = builder.ToString();
        return true;
    }
}
