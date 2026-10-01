using System.Globalization;
using System.Text;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Search;

/// <summary>A word or phrase of the free-text part of a query.</summary>
/// <param name="Text">Cleaned text; a phrase keeps its inner spaces.</param>
/// <param name="IsPhrase">Whether the user wrote it between quotes.</param>
/// <param name="IsExcluded">Whether it was prefixed with '-': notes containing it are left out.</param>
/// <param name="IsPrefix">Whether it is being typed and should match by prefix.</param>
public sealed record SearchTerm(string Text, bool IsPhrase, bool IsExcluded, bool IsPrefix);

/// <summary>What the user typed in the search box, split into free text and structured filters.</summary>
public sealed record ParsedSearch(IReadOnlyList<SearchTerm> Terms, NoteFilter Filter)
{
    public static ParsedSearch Empty { get; } = new([], NoteFilter.Empty);

    public bool HasIncludedTerms => Terms.Any(term => !term.IsExcluded);

    public bool HasExcludedTerms => Terms.Any(term => term.IsExcluded);

    public bool IsEmpty => Terms.Count == 0 && Filter.IsEmpty;
}

/// <summary>
/// Parses the search syntax: free words, <c>"exact phrases"</c>, <c>-excluded</c> words or phrases,
/// <c>#tag</c>, and <c>key:value</c> filters (<c>project:</c>, <c>tag:</c>, <c>type:</c>,
/// <c>commit:</c>, <c>ticket:</c>, <c>created:</c>, <c>updated:</c>, with Spanish aliases). A value
/// with spaces goes between quotes: <c>project:"azure microservices"</c>. Dates accept
/// <c>2026-09-01</c>, <c>2026-09</c>, <c>2026</c>, the comparisons <c>&gt;</c>, <c>&gt;=</c>,
/// <c>&lt;</c>, <c>&lt;=</c> and the range <c>2026-09-01..2026-09-30</c>.
/// Anything that is not a recognized filter is ordinary text, so a query can never fail to parse.
/// </summary>
public static class SearchQueryParser
{
    public const int MaxInputLength = 512;

    /// <summary>Free-text terms kept (phrases count as one); more only slow the query down.</summary>
    public const int MaxTerms = FtsQueryBuilder.MaxTerms;

    private const int MaxValueLength = 200;

    public static ParsedSearch Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ParsedSearch.Empty;
        }

        var text = input.AsSpan();
        if (text.Length > MaxInputLength)
        {
            text = text[..MaxInputLength];
        }

        var terms = new List<SearchTerm>();
        var filter = new FilterBuilder();
        var position = 0;
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            var excluded = false;
            if (text[position] == '-' && position + 1 < text.Length && !char.IsWhiteSpace(text[position + 1]))
            {
                excluded = true;
                position++;
            }

            if (text[position] == '"')
            {
                var phrase = ReadQuoted(text, ref position, out var closed);
                AddPhrase(terms, phrase, excluded, isPrefix: !closed);
                continue;
            }

            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            var token = text[start..position];
            var colon = token.IndexOf(':');
            if (colon > 0 && TryGetFilterKey(token[..colon], out var key))
            {
                ReadOnlySpan<char> value;
                if (colon + 1 < token.Length && token[colon + 1] == '"')
                {
                    // The value is quoted and may contain spaces: continue from the opening quote.
                    position = start + colon + 1;
                    value = ReadQuoted(text, ref position, out _);
                }
                else
                {
                    value = token[(colon + 1)..];
                }

                if (!excluded)
                {
                    // Negated filters are not supported; "-project:x" is ignored rather than guessed at.
                    filter.Add(key, value.ToString());
                }

                continue;
            }

            if (!excluded && token.Length > 1 && token[0] == '#' && char.IsLetter(token[1]))
            {
                filter.Add(FilterKey.Tag, token[1..].ToString());
                continue;
            }

            AddWord(terms, token, excluded, isPrefix: position >= text.Length);
        }

        MarkPrefix(terms);
        return new ParsedSearch(terms, filter.Build());
    }

    private static void AddWord(List<SearchTerm> terms, ReadOnlySpan<char> token, bool excluded, bool isPrefix)
    {
        var word = FtsQueryBuilder.CleanTerm(token);
        if (word.Length > 0 && terms.Count < MaxTerms)
        {
            terms.Add(new SearchTerm(word, IsPhrase: false, excluded, isPrefix));
        }
    }

    private static void AddPhrase(List<SearchTerm> terms, ReadOnlySpan<char> phrase, bool excluded, bool isPrefix)
    {
        var words = new StringBuilder(phrase.Length);
        foreach (var range in phrase.SplitAny(FtsQueryBuilder.Separators))
        {
            var word = FtsQueryBuilder.CleanTerm(phrase[range]);
            if (word.Length == 0)
            {
                continue;
            }

            if (words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(word);
        }

        if (words.Length > 0 && terms.Count < MaxTerms)
        {
            terms.Add(new SearchTerm(words.ToString(), IsPhrase: true, excluded, isPrefix));
        }
    }

    /// <summary>Only the term at the end of the input matches by prefix, and never an excluded one.</summary>
    private static void MarkPrefix(List<SearchTerm> terms)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            var isLast = i == terms.Count - 1;
            var isPrefix = isLast && !term.IsExcluded && (term.IsPrefix || !term.IsPhrase);
            if (term.IsPrefix != isPrefix)
            {
                terms[i] = term with { IsPrefix = isPrefix };
            }
        }
    }

    /// <summary>Reads from an opening quote to the closing one (or the end of the input).</summary>
    private static ReadOnlySpan<char> ReadQuoted(ReadOnlySpan<char> text, scoped ref int position, out bool closed)
    {
        var start = position + 1;
        var end = text[start..].IndexOf('"');
        if (end < 0)
        {
            closed = false;
            position = text.Length;
            return text[start..];
        }

        closed = true;
        position = start + end + 1;
        return text[start..(start + end)];
    }

    private static bool TryGetFilterKey(ReadOnlySpan<char> name, out FilterKey key)
    {
        Span<char> lower = stackalloc char[16];
        if (name.Length > lower.Length)
        {
            key = default;
            return false;
        }

        var length = name.ToLowerInvariant(lower);
        key = lower[..length] switch
        {
            "project" or "proyecto" or "p" => FilterKey.Project,
            "tag" or "tags" or "etiqueta" or "t" => FilterKey.Tag,
            "type" or "tipo" => FilterKey.Type,
            "commit" => FilterKey.Commit,
            "ticket" => FilterKey.Ticket,
            "created" or "creada" or "creado" => FilterKey.Created,
            "updated" or "actualizada" or "actualizado" => FilterKey.Updated,
            _ => FilterKey.None,
        };
        return key != FilterKey.None;
    }

    private enum FilterKey
    {
        None,
        Project,
        Tag,
        Type,
        Commit,
        Ticket,
        Created,
        Updated,
    }

    private sealed class FilterBuilder
    {
        private readonly List<string> _projects = [];
        private readonly List<Tag> _tags = [];
        private readonly List<NoteType> _types = [];
        private readonly List<string> _commits = [];
        private readonly List<string> _tickets = [];
        private DateRange _created = DateRange.Empty;
        private DateRange _updated = DateRange.Empty;
        private bool _unsatisfiable;

        public void Add(FilterKey key, string rawValue)
        {
            var value = rawValue.Trim();
            if (value.Length == 0)
            {
                return; // "project:" with nothing after it is still being typed.
            }

            if (value.Length > MaxValueLength)
            {
                value = value[..MaxValueLength];
            }

            switch (key)
            {
                case FilterKey.Project:
                    if (NoteTitle.TryNormalize(value, out var project))
                    {
                        AddDistinct(_projects, project);
                    }

                    break;
                case FilterKey.Tag:
                    foreach (var tag in Tag.NormalizeMany(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                    {
                        if (!_tags.Contains(tag))
                        {
                            _tags.Add(tag);
                        }
                    }

                    break;
                case FilterKey.Type:
                    if (NoteTypes.TryParse(value, out var type))
                    {
                        if (!_types.Contains(type))
                        {
                            _types.Add(type);
                        }
                    }
                    else
                    {
                        _unsatisfiable = true; // No note has a type the app does not know.
                    }

                    break;
                case FilterKey.Commit:
                    AddDistinct(_commits, value);
                    break;
                case FilterKey.Ticket:
                    AddDistinct(_tickets, value);
                    break;
                case FilterKey.Created:
                    if (TryParseDateFilter(value, out var created))
                    {
                        _created = _created.Intersect(created);
                    }

                    break;
                case FilterKey.Updated:
                    if (TryParseDateFilter(value, out var updated))
                    {
                        _updated = _updated.Intersect(updated);
                    }

                    break;
                default:
                    break;
            }
        }

        public NoteFilter Build() => new()
        {
            Projects = _projects,
            Tags = _tags,
            Types = _types,
            Commits = _commits,
            Tickets = _tickets,
            Created = _created,
            Updated = _updated,
            IsUnsatisfiable = _unsatisfiable || _created.IsUnsatisfiable || _updated.IsUnsatisfiable,
        };

        private static void AddDistinct(List<string> values, string value)
        {
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        /// <summary>Malformed dates are ignored (the user is probably still typing) rather than matching nothing.</summary>
        private static bool TryParseDateFilter(string value, out DateRange range)
        {
            range = DateRange.Empty;
            var span = value.AsSpan();

            var separator = span.IndexOf("..", StringComparison.Ordinal);
            if (separator > 0)
            {
                if (TryParseDatePeriod(span[..separator], out var first) && TryParseDatePeriod(span[(separator + 2)..], out var last))
                {
                    range = new DateRange(first.From, last.To);
                    return true;
                }

                return false;
            }

            var comparison = 0;
            while (comparison < span.Length && span[comparison] is '<' or '>' or '=')
            {
                comparison++;
            }

            if (!TryParseDatePeriod(span[comparison..], out var period))
            {
                return false;
            }

            var from = period.From!.Value;
            var to = period.To!.Value;
            range = span[..comparison] switch
            {
                "" or "=" => period,
                ">" => new DateRange(new DateBound(to.Date, Inclusive: false), null),
                ">=" => new DateRange(new DateBound(from.Date, Inclusive: true), null),
                "<" => new DateRange(null, new DateBound(from.Date, Inclusive: false)),
                "<=" => new DateRange(null, new DateBound(to.Date, Inclusive: true)),
                _ => DateRange.Empty,
            };
            return !range.IsEmpty;
        }

        /// <summary>A day, a month or a year, as the closed range of days it covers.</summary>
        private static bool TryParseDatePeriod(ReadOnlySpan<char> text, out DateRange period)
        {
            period = DateRange.Empty;
            var culture = CultureInfo.InvariantCulture;
            switch (text.Length)
            {
                case 10 when DateOnly.TryParseExact(text, "yyyy-MM-dd", culture, DateTimeStyles.None, out var day):
                    period = DateRange.Exactly(day);
                    return true;
                case 7 when DateOnly.TryParseExact(text, "yyyy-MM", culture, DateTimeStyles.None, out var month):
                    period = DateRange.Between(month, month.AddMonths(1).AddDays(-1));
                    return true;
                case 4 when int.TryParse(text, NumberStyles.None, culture, out var year) && year is >= 1 and <= 9999:
                    period = DateRange.Between(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
                    return true;
                default:
                    return false;
            }
        }
    }
}
