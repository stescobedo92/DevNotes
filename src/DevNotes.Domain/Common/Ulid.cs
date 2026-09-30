using System.Security.Cryptography;

namespace DevNotes.Domain.Common;

/// <summary>
/// Minimal ULID generator (48-bit millisecond timestamp + 80 bits of randomness, Crockford base32).
/// Identifiers generated within the same millisecond are monotonically increasing.
/// </summary>
public static class Ulid
{
    public const int Length = 26;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const long MaxTimestamp = (1L << 48) - 1;

    private static readonly Lock _gate = new();
    private static readonly byte[] _lastRandom = new byte[10];
    private static long _lastTimestamp = -1;

    public static string NewUlid(DateTimeOffset timestamp)
    {
        var milliseconds = timestamp.ToUnixTimeMilliseconds();
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds, nameof(timestamp));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, MaxTimestamp, nameof(timestamp));

        Span<byte> random = stackalloc byte[10];
        lock (_gate)
        {
            if (milliseconds > _lastTimestamp)
            {
                _lastTimestamp = milliseconds;
                RandomNumberGenerator.Fill(_lastRandom);
            }
            else
            {
                // Same (or earlier) millisecond: keep ordering by incrementing the random component.
                milliseconds = _lastTimestamp;
                if (!TryIncrement(_lastRandom))
                {
                    // 80-bit overflow is practically unreachable; move to the next millisecond.
                    milliseconds = ++_lastTimestamp;
                    RandomNumberGenerator.Fill(_lastRandom);
                }
            }

            _lastRandom.CopyTo(random);
        }

        Span<char> chars = stackalloc char[Length];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(milliseconds & 31)];
            milliseconds >>= 5;
        }

        EncodeFiveBytes(random[..5], chars.Slice(10, 8));
        EncodeFiveBytes(random[5..], chars.Slice(18, 8));
        return new string(chars);
    }

    public static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.Length != Length || char.ToUpperInvariant(value[0]) > '7')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!Alphabet.Contains(char.ToUpperInvariant(c), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void EncodeFiveBytes(ReadOnlySpan<byte> source, Span<char> destination)
    {
        var bits = ((ulong)source[0] << 32) | ((ulong)source[1] << 24) | ((ulong)source[2] << 16)
            | ((ulong)source[3] << 8) | source[4];
        for (var i = 7; i >= 0; i--)
        {
            destination[i] = Alphabet[(int)(bits & 31)];
            bits >>= 5;
        }
    }

    private static bool TryIncrement(Span<byte> bigEndian)
    {
        for (var i = bigEndian.Length - 1; i >= 0; i--)
        {
            if (++bigEndian[i] != 0)
            {
                return true;
            }
        }

        return false;
    }
}
