namespace DevNotes.Domain.Common;

/// <summary>
/// Portable file-name rules. A vault must stay valid when it is cloned on another operating
/// system, so the strictest platform (Windows) defines what is allowed everywhere.
/// </summary>
internal static class FileNameRules
{
    private static readonly HashSet<string> _reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsReservedDeviceName(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot >= 0 ? segment[..dot] : segment;
        return _reservedDeviceNames.GetAlternateLookup<ReadOnlySpan<char>>().Contains(stem.TrimEnd(' '));
    }

    /// <summary>Returns null when the segment is valid, otherwise a short reason.</summary>
    public static string? ValidateSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty)
        {
            return "empty path segment";
        }

        if (segment is "." or "..")
        {
            return "relative navigation segments are not allowed";
        }

        if (segment[0] == ' ' || segment[^1] is ' ' or '.')
        {
            return "segments cannot start with a space or end with a space or dot";
        }

        foreach (var c in segment)
        {
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/' or '\u007f')
            {
                return "segment contains a character that is not portable across file systems";
            }
        }

        return IsReservedDeviceName(segment) ? "segment is a reserved device name" : null;
    }
}
