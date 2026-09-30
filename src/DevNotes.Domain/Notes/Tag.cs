using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DevNotes.Domain.Notes;

/// <summary>
/// Normalized tag: lower-case, no leading '#', inner whitespace collapsed to '-'.
/// Developer-style tags such as <c>c#</c>, <c>c++</c> or <c>.net</c> are valid.
/// </summary>
public readonly record struct Tag : IComparable<Tag>
{
    public const int MaxLength = 64;

    private readonly string? _value;

    private Tag(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public static Tag Create(string text) =>
        TryCreate(text, out var tag)
            ? tag
            : throw new ArgumentException($"'{text}' is not a valid tag.", nameof(text));

    public static bool TryCreate([NotNullWhen(true)] string? text, out Tag tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span[0] == '#')
        {
            span = span[1..].TrimStart();
        }

        var builder = new StringBuilder(span.Length);
        var pendingSeparator = false;
        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c) || c is ',' or '[' or ']' or '"' or '\'' or '`')
            {
                return false;
            }

            if (pendingSeparator)
            {
                builder.Append('-');
                pendingSeparator = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        if (builder.Length is 0 or > MaxLength)
        {
            return false;
        }

        tag = new Tag(builder.ToString());
        return true;
    }

    /// <summary>Normalizes a sequence of raw tags, dropping invalid entries and duplicates while keeping order.</summary>
    public static IReadOnlyList<Tag> NormalizeMany(IEnumerable<string?> rawTags)
    {
        ArgumentNullException.ThrowIfNull(rawTags);

        var result = new List<Tag>();
        var seen = new HashSet<Tag>();
        foreach (var raw in rawTags)
        {
            if (TryCreate(raw, out var tag) && seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    public int CompareTo(Tag other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(Tag left, Tag right) => left.CompareTo(right) < 0;

    public static bool operator <=(Tag left, Tag right) => left.CompareTo(right) <= 0;

    public static bool operator >(Tag left, Tag right) => left.CompareTo(right) > 0;

    public static bool operator >=(Tag left, Tag right) => left.CompareTo(right) >= 0;
}
