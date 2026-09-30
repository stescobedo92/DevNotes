using System.Text;
using DevNotes.Application.Common;

namespace DevNotes.Application.Notes;

public enum FrontmatterPlacement
{
    /// <summary>A new key is inserted as the first line of the block.</summary>
    Start,

    /// <summary>A new key is appended as the last line of the block.</summary>
    End,
}

/// <param name="Key">Top-level key name.</param>
/// <param name="FormattedValue">Value already formatted as a single-line YAML scalar (see <see cref="YamlScalar"/>).</param>
/// <param name="Placement">Where the key goes when it does not exist yet.</param>
public readonly record struct FrontmatterAssignment(
    string Key,
    string FormattedValue,
    FrontmatterPlacement Placement = FrontmatterPlacement.End);

/// <summary>
/// Edits top-level scalar keys of the frontmatter as text. Everything else in the block
/// (unknown keys, comments, ordering, formatting) is left exactly as the user wrote it, which a
/// parse → serialize round-trip cannot guarantee.
/// </summary>
public static class FrontmatterEditor
{
    public static string SetScalars(string text, IReadOnlyList<FrontmatterAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0)
        {
            return text;
        }

        var newline = DetectNewline(text);
        return FrontmatterBlock.TryFind(text, out var block)
            ? UpdateBlock(text, block, assignments, newline)
            : CreateBlock(text, assignments, newline);
    }

    private static string CreateBlock(string text, IReadOnlyList<FrontmatterAssignment> assignments, string newline)
    {
        var hasBom = text.Length > 0 && text[0] == TextConstants.ByteOrderMark;
        var content = hasBom ? text.AsSpan(1) : text.AsSpan();

        var builder = new StringBuilder(text.Length + 64);
        if (hasBom)
        {
            builder.Append(TextConstants.ByteOrderMark);
        }

        builder.Append("---").Append(newline);
        foreach (var assignment in Ordered(assignments))
        {
            builder.Append(assignment.Key).Append(": ").Append(assignment.FormattedValue).Append(newline);
        }

        builder.Append("---").Append(newline);
        if (!content.IsEmpty && content[0] is not ('\n' or '\r'))
        {
            builder.Append(newline);
        }

        return builder.Append(content).ToString();
    }

    private static string UpdateBlock(string text, FrontmatterBlock block, IReadOnlyList<FrontmatterAssignment> assignments, string newline)
    {
        var lines = SplitLines(text.AsSpan(block.YamlStart, block.YamlLength));
        var insertAtStart = 0;

        foreach (var assignment in assignments)
        {
            var index = FindKey(lines, assignment.Key, out var writtenKey);
            if (index >= 0)
            {
                var end = FindExtentEnd(lines, index);
                lines.RemoveRange(index, end - index);
                lines.Insert(index, $"{writtenKey}: {assignment.FormattedValue}");
            }
            else if (assignment.Placement == FrontmatterPlacement.Start)
            {
                lines.Insert(insertAtStart++, $"{assignment.Key}: {assignment.FormattedValue}");
            }
            else
            {
                lines.Add($"{assignment.Key}: {assignment.FormattedValue}");
            }
        }

        var builder = new StringBuilder(text.Length + 64);
        builder.Append(text, 0, block.YamlStart);
        foreach (var line in lines)
        {
            builder.Append(line).Append(newline);
        }

        return builder.Append(text, block.YamlEnd, text.Length - block.YamlEnd).ToString();
    }

    private static IEnumerable<FrontmatterAssignment> Ordered(IReadOnlyList<FrontmatterAssignment> assignments) =>
        assignments.Where(a => a.Placement == FrontmatterPlacement.Start)
            .Concat(assignments.Where(a => a.Placement == FrontmatterPlacement.End));

    private static List<string> SplitLines(ReadOnlySpan<char> yaml)
    {
        // Only LF / CRLF are line terminators here; other Unicode separators stay inside their line.
        var lines = new List<string>();
        foreach (var range in yaml.Split('\n'))
        {
            lines.Add(yaml[range].TrimEnd('\r').ToString());
        }

        // The block ends with a line terminator, which yields a trailing empty entry.
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static int FindKey(List<string> lines, string key, out string writtenKey)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].AsSpan();
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = line[key.Length..].TrimStart(" \t");
            if (rest.Length > 0 && rest[0] == ':' && (rest.Length == 1 || rest[1] is ' ' or '\t'))
            {
                writtenKey = lines[i][..key.Length];
                return i;
            }
        }

        writtenKey = key;
        return -1;
    }

    /// <summary>Returns the index after the last line that belongs to the value of the key at <paramref name="keyIndex"/>.</summary>
    private static int FindExtentEnd(List<string> lines, int keyIndex)
    {
        var keyLine = lines[keyIndex].AsSpan();
        var value = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
        var valueIsEmpty = value.IsEmpty || value[0] == '#';

        var end = keyIndex + 1;
        var index = end;
        while (index < lines.Count)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++; // Blank lines only belong to the value if a continuation line follows.
                continue;
            }

            if (!IsContinuation(line, valueIsEmpty))
            {
                break;
            }

            end = ++index;
        }

        return end;
    }

    private static bool IsContinuation(string line, bool valueIsEmpty) =>
        line[0] is ' ' or '\t'
        || (valueIsEmpty && line[0] == '-' && (line.Length == 1 || line[1] is ' ' or '\t'));

    private static string DetectNewline(string text)
    {
        var index = text.IndexOf('\n', StringComparison.Ordinal);
        return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
    }
}
