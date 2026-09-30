using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace DevNotes.Domain.Notes;

/// <summary>SHA-256 of the exact bytes of a note file; drives incremental indexing and conflict detection.</summary>
public readonly record struct ContentHash
{
    private static readonly SearchValues<char> _lowerHexDigits = SearchValues.Create("0123456789abcdef");

    private readonly string? _hex;

    private ContentHash(string hex) => _hex = hex;

    public string Hex => _hex ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_hex);

    public static ContentHash Compute(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, hash);
        return new ContentHash(Convert.ToHexStringLower(hash));
    }

    /// <summary>Hash of the UTF-8 encoding (without BOM) of <paramref name="text"/>.</summary>
    public static ContentHash Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Compute(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Wraps a hash read back from the index; validates the hexadecimal shape.</summary>
    public static ContentHash FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != SHA256.HashSizeInBytes * 2 || hex.AsSpan().ContainsAnyExcept(_lowerHexDigits))
        {
            throw new FormatException("A content hash must be 64 lower-case hexadecimal characters.");
        }

        return new ContentHash(hex);
    }

    public override string ToString() => Hex;
}
