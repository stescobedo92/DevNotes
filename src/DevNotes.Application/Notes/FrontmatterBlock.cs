using DevNotes.Application.Common;

namespace DevNotes.Application.Notes;

public enum FrontmatterScan
{
    /// <summary>The note does not start with a frontmatter fence.</summary>
    None,

    /// <summary>A complete block was found.</summary>
    Found,

    /// <summary>The note starts a frontmatter block (fence followed by a <c>key:</c> line) but never closes it.</summary>
    Unterminated,

    /// <summary>The block is closed but larger than <see cref="FrontmatterBlock.MaxYamlLength"/>.</summary>
    TooLarge,
}

/// <summary>
/// Location of the YAML frontmatter inside a note: the text between the opening <c>---</c> line
/// and the closing <c>---</c> (or <c>...</c>) line.
/// </summary>
/// <param name="YamlStart">Offset of the first character after the opening fence line.</param>
/// <param name="YamlLength">Length of the YAML text (without the closing fence).</param>
/// <param name="BodyStart">Offset of the first character after the closing fence line.</param>
public readonly record struct FrontmatterBlock(int YamlStart, int YamlLength, int BodyStart)
{
    /// <summary>Frontmatter larger than this is treated as regular content; it protects the YAML parser.</summary>
    public const int MaxYamlLength = 64 * 1024;

    public int YamlEnd => YamlStart + YamlLength;

    /// <summary>
    /// Finds the frontmatter of <paramref name="text"/>. It must start on the very first line
    /// (an optional byte-order mark is tolerated).
    /// </summary>
    public static bool TryFind(string text, out FrontmatterBlock block) => Scan(text, out block) == FrontmatterScan.Found;

    /// <summary>
    /// Like <see cref="TryFind"/>, but also reports a block that was started and cannot be used. Such
    /// a note must not be given a second block on top: the user is probably still typing the first one.
    /// </summary>
    public static FrontmatterScan Scan(string text, out FrontmatterBlock block)
    {
        ArgumentNullException.ThrowIfNull(text);
        block = default;

        var position = text.Length > 0 && text[0] == TextConstants.ByteOrderMark ? 1 : 0;
        if (!TryReadLine(text, position, out var firstLine, out var next) || !IsFence(firstLine, allowDots: false))
        {
            return FrontmatterScan.None;
        }

        var yamlStart = next;
        while (TryReadLine(text, next, out var line, out var afterLine))
        {
            if (IsFence(line, allowDots: true))
            {
                var yamlLength = next - yamlStart;
                if (yamlLength > MaxYamlLength)
                {
                    return FrontmatterScan.TooLarge;
                }

                block = new FrontmatterBlock(yamlStart, yamlLength, afterLine);
                return FrontmatterScan.Found;
            }

            next = afterLine;
        }

        // No closing fence. A note may legitimately begin with a horizontal rule, so the block only
        // counts as "being written" when what follows the fence looks like a YAML key.
        return StartsWithKey(text.AsSpan(yamlStart)) ? FrontmatterScan.Unterminated : FrontmatterScan.None;
    }

    /// <summary>True when the first non-blank line has the shape <c>key:</c> or <c>key: value</c>.</summary>
    private static bool StartsWithKey(ReadOnlySpan<char> text)
    {
        foreach (var rawLine in text.EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0 || (colon + 1 < line.Length && line[colon + 1] is not (' ' or '\t')))
            {
                return false;
            }

            foreach (var c in line[..colon])
            {
                if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '"' or '\''))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static bool IsFence(ReadOnlySpan<char> line, bool allowDots)
    {
        var trimmed = line.TrimEnd(" \t");
        return trimmed is "---" || (allowDots && trimmed is "...");
    }

    /// <summary>Reads the line starting at <paramref name="start"/> without its terminator.</summary>
    private static bool TryReadLine(string text, int start, out ReadOnlySpan<char> line, out int next)
    {
        if (start >= text.Length)
        {
            line = default;
            next = start;
            return false;
        }

        var remaining = text.AsSpan(start);
        var newline = remaining.IndexOf('\n');
        if (newline < 0)
        {
            line = remaining;
            next = text.Length;
            return true;
        }

        line = newline > 0 && remaining[newline - 1] == '\r' ? remaining[..(newline - 1)] : remaining[..newline];
        next = start + newline + 1;
        return true;
    }
}
