using System.Text;

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
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        // Line breaks, tabs and runs of spaces collapse into single spaces; nothing leads or trails.
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is MatchStart or MatchEnd)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length + 1 >= maxLength)
                {
                    break;
                }

                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString();
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
