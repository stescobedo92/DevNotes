using System.Buffers;
using System.Text;
using DevNotes.Application.Common;

namespace DevNotes.Application.Search;

/// <summary>
/// Sanitized FTS5 <c>MATCH</c> expressions for one user query.
/// <see cref="Match"/> targets the word index (unicode61); <see cref="TrigramMatch"/> targets the
/// substring index and is null when no term is long enough for trigrams; <see cref="Exclude"/>
/// matches, on the word index, the notes that must be left out.
/// </summary>
public sealed record FtsQuery(string? Match, string? TrigramMatch, string? Exclude = null)
{
    public static FtsQuery Empty { get; } = new(null, null);

    /// <summary>No positive text to search for (there may still be exclusions).</summary>
    public bool IsEmpty => Match is null;
}

/// <summary>
/// Turns the parsed search into safe FTS5 queries.
/// Every term is emitted as a quoted string, so FTS5 operators (AND, OR, NOT, NEAR, column
/// filters, '*', '^', parentheses…) typed by the user are always treated as plain text.
/// The resulting expression is still passed to SQLite as a bound parameter.
/// </summary>
public static class FtsQueryBuilder
{
    public const int MaxTerms = 12;
    public const int MaxTermLength = 64;
    public const int MinTrigramLength = 3;

    internal static readonly SearchValues<char> Separators =
        SearchValues.Create([' ', '\t', '\r', '\n', TextConstants.NoBreakSpace]);

    /// <summary>Parses free text and builds the expressions in one step.</summary>
    public static FtsQuery Build(string? input) => Build(SearchQueryParser.Parse(input));

    public static FtsQuery Build(ParsedSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var match = new StringBuilder();
        var trigram = new StringBuilder();
        var exclude = new StringBuilder();
        foreach (var term in search.Terms)
        {
            if (term.IsExcluded)
            {
                // Alternatives: a note containing any excluded word or phrase is left out.
                Append(exclude, term.Text, prefix: false, separator: " OR ");
                continue;
            }

            Append(match, term.Text, term.IsPrefix, separator: " ");
            if (term.Text.Length >= MinTrigramLength)
            {
                Append(trigram, term.Text, prefix: false, separator: " ");
            }
        }

        return new FtsQuery(
            match.Length > 0 ? match.ToString() : null,
            trigram.Length > 0 ? trigram.ToString() : null,
            exclude.Length > 0 ? exclude.ToString() : null);
    }

    private static void Append(StringBuilder expression, string term, bool prefix, string separator)
    {
        if (expression.Length > 0)
        {
            expression.Append(separator);
        }

        // A quoted string with spaces is a phrase for FTS5; "*" after it matches the last word by prefix.
        expression.Append('"').Append(term).Append('"');
        if (prefix)
        {
            expression.Append('*');
        }
    }

    /// <summary>
    /// Removes what cannot live inside an FTS5 string (quotes, control characters) and rejects
    /// terms without any letter or digit, which would tokenize to an empty phrase.
    /// </summary>
    internal static string CleanTerm(ReadOnlySpan<char> term)
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
