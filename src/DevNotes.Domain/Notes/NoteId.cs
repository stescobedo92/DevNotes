using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using DevNotes.Domain.Common;

namespace DevNotes.Domain.Notes;

/// <summary>
/// Stable identity of a note. Notes created by the app get a ULID; identifiers written by
/// other tools are accepted as long as they are safe tokens (letters, digits, '.', '_', '-').
/// </summary>
public readonly record struct NoteId
{
    private const string PathDerivedPrefix = "path-";

    private readonly string? _value;

    private NoteId(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>
    /// True for identifiers derived from the file path (notes whose frontmatter has no <c>id</c> yet).
    /// They change when the file is renamed, so they are replaced by a ULID on the first save.
    /// </summary>
    public bool IsPathDerived => Value.StartsWith(PathDerivedPrefix, StringComparison.Ordinal);

    public static NoteId NewId(DateTimeOffset timestamp) => new(Ulid.NewUlid(timestamp));

    /// <summary>Deterministic identifier for a note that has no <c>id</c> in its frontmatter.</summary>
    public static NoteId FromPath(NotePath path)
    {
        if (path.IsEmpty)
        {
            throw new ArgumentException("Path is empty.", nameof(path));
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(path.Value), hash);
        return new NoteId(PathDerivedPrefix + Convert.ToHexStringLower(hash[..12]));
    }

    public static NoteId Parse(string text) =>
        TryParse(text, out var id)
            ? id
            : throw new FormatException($"'{text}' is not a valid note id.");

    public static bool TryParse([NotNullWhen(true)] string? text, out NoteId id)
    {
        var trimmed = text?.Trim();
        if (trimmed is not null && SafeToken.IsValid(trimmed))
        {
            id = new NoteId(trimmed);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
