using System.Buffers;
using System.Text;
using DevNotes.Application.Common;

namespace DevNotes.Application.Search;

/// <summary>
/// Sanitized FTS5 <c>MATCH</c> expressions for one user query.
/// <see cref="Match"/> targets the word index (unicode61); <see cref="TrigramMatch"/> targets the
/// substring index and is null when no term is long enough for trigrams.
/// </summary>
public sealed record FtsQuery(string? Match, string? TrigramMatch)
{
    public static FtsQuery Empty { get; } = new(null, null);

    public bool IsEmpty => Match is null;
}

/// <summary>
/// Turns free text typed by the user into safe FTS5 queries.
/// Every term is emitted as a quoted string, so FTS5 operators (AND, OR, NOT, NEAR, column
/// filters, '*', '^', parentheses…) typed by the user are always treated as plain text.
/// The resulting expression is still passed to SQLite as a bound parameter.
/// </summary>
public static class FtsQueryBuilder
{
    public const int MaxInputLength = 256;
    public const int MaxTerms = 12;
    public const int MaxTermLength = 64;
    public const int MinTrigramLength = 3;

    private static readonly SearchValues<char> _separators =
        SearchValues.Create([' ', '\t', '\r', '\n', TextConstants.NoBreakSpace]);

    public static FtsQuery Build(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return FtsQuery.Empty;
        }

        var span = input.AsSpan();
        if (span.Length > MaxInputLength)
        {
            span = span[..MaxInputLength];
        }

        var terms = new List<string>(capacity: 4);
        foreach (var range in span.SplitAny(_separators))
        {
            var term = Clean(span[range]);
            if (term.Length == 0)
            {
                continue;
            }

            terms.Add(term);
            if (terms.Count == MaxTerms)
            {
                break;
            }
        }

        if (terms.Count == 0)
        {
            return FtsQuery.Empty;
        }

        var match = new StringBuilder();
        var trigram = new StringBuilder();
        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            if (match.Length > 0)
            {
                match.Append(' ');
            }

            match.Append('"').Append(term).Append('"');

            // Search-as-you-type: the term being typed matches by prefix.
            if (i == terms.Count - 1)
            {
                match.Append('*');
            }

            if (term.Length >= MinTrigramLength)
            {
                if (trigram.Length > 0)
                {
                    trigram.Append(' ');
                }

                trigram.Append('"').Append(term).Append('"');
            }
        }

        return new FtsQuery(match.ToString(), trigram.Length > 0 ? trigram.ToString() : null);
    }

    /// <summary>
    /// Removes what cannot live inside an FTS5 string (quotes, control characters) and rejects
    /// terms without any letter or digit, which would tokenize to an empty phrase.
    /// </summary>
    private static string Clean(ReadOnlySpan<char> term)
    {
        if (term.Length > MaxTermLength)
        {
            term = term[..MaxTermLength];
        }

        var builder = new StringBuilder(term.Length);
        var hasWordCharacter = false;
        for (var i = 0; i < term.Length; i++)
        {
            var c = term[i];
            if (c == '"' || char.IsControl(c))
            {
                continue;
            }

            if (char.IsSurrogate(c))
            {
                // Keep well-formed pairs only; truncation or bad input may leave a lone surrogate.
                if (char.IsHighSurrogate(c) && i + 1 < term.Length && char.IsLowSurrogate(term[i + 1]))
                {
                    hasWordCharacter |= Rune.IsLetterOrDigit(new Rune(c, term[i + 1]));
                    builder.Append(c).Append(term[i + 1]);
                    i++;
                }

                continue;
            }

            hasWordCharacter |= char.IsLetterOrDigit(c);
            builder.Append(c);
        }

        return hasWordCharacter ? builder.ToString() : string.Empty;
    }
}
