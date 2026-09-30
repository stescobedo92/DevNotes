namespace DevNotes.Application.Search;

/// <summary>
/// Splits the text returned by FTS5 <c>snippet()</c> into plain and highlighted segments.
/// Private-use code points delimit the matches so that note content can never be confused
/// with markup (there is no HTML or escaping involved).
/// </summary>
public static class SnippetParser
{
    public const char MatchStart = (char)0xE000;
    public const char MatchEnd = (char)0xE001;
    public const string Ellipsis = "…";

    public static IReadOnlyList<SnippetSegment> Parse(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet))
        {
            return [];
        }

        var segments = new List<SnippetSegment>();
        var span = snippet.AsSpan();
        var isMatch = false;
        var start = 0;

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c != MatchStart && c != MatchEnd)
            {
                continue;
            }

            Append(segments, span[start..i], isMatch);
            isMatch = c == MatchStart;
            start = i + 1;
        }

        Append(segments, span[start..], isMatch);
        return segments;
    }

    /// <summary>Single-line version of a body fragment for list rows.</summary>
    public static string ToSingleLine(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return string.Create(Math.Min(text.Length, maxLength), text, static (destination, source) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var c = source[i];
                destination[i] = char.IsControl(c) || c is MatchStart or MatchEnd ? ' ' : c;
            }
        });
    }

    private static void Append(List<SnippetSegment> segments, ReadOnlySpan<char> text, bool isMatch)
    {
        if (text.IsEmpty)
        {
            return;
        }

        // Newlines inside a fragment would break the single-line list row.
        var clean = string.Create(text.Length, text, static (destination, source) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
        segments.Add(new SnippetSegment(clean, isMatch));
    }
}
