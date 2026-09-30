using System.Globalization;
using DevNotes.Application.Common;
using DevNotes.Domain.Notes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DevNotes.Application.Notes;

/// <summary>
/// Parses the text of a note: YAML frontmatter (read-only, via the YamlDotNet representation
/// model so no reflection or type activation is involved) plus the Markdown body.
/// Parsing never throws for malformed content: a note with broken frontmatter is still a note.
/// </summary>
public static class NoteDocumentParser
{
    private const int MaxListItems = 256;
    private const int MaxFlowNesting = 32;

    /// <param name="text">Full text of the note file.</param>
    /// <param name="fallbackTitle">Used when neither the frontmatter nor a level-1 heading provides a title.</param>
    public static NoteDocument Parse(string text, string fallbackTitle)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackTitle);

        if (!FrontmatterBlock.TryFind(text, out var block))
        {
            var start = text.Length > 0 && text[0] == TextConstants.ByteOrderMark ? 1 : 0;
            var plainBody = text[start..];
            return new NoteDocument(
                new NoteMetadata { Title = ResolveTitle(null, plainBody, fallbackTitle) },
                plainBody,
                start,
                FrontmatterStatus.None,
                FrontmatterError: null);
        }

        if (!TryLoadMapping(text.Substring(block.YamlStart, block.YamlLength), out var mapping, out var error))
        {
            // Keep the whole text searchable: the block may be ordinary content delimited by rules.
            return new NoteDocument(
                new NoteMetadata { Title = ResolveTitle(null, text, fallbackTitle) },
                text,
                BodyOffset: 0,
                FrontmatterStatus.Invalid,
                error);
        }

        var body = text[block.BodyStart..];
        var metadata = ReadMetadata(mapping, body, fallbackTitle);
        return new NoteDocument(metadata, body, block.BodyStart, FrontmatterStatus.Valid, FrontmatterError: null);
    }

    /// <summary>Returns the text of the first level-1 ATX heading that is not inside a fenced code block.</summary>
    public static string? FindFirstHeading(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        char fenceChar = default;
        foreach (var rawLine in body.AsSpan().EnumerateLines())
        {
            var line = rawLine.TrimStart(' ');
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fenceChar == default)
                {
                    fenceChar = line[0];
                }
                else if (fenceChar == line[0])
                {
                    fenceChar = default;
                }

                continue;
            }

            if (fenceChar != default || rawLine.Length - line.Length > 3)
            {
                continue;
            }

            if (line.Length > 2 && line[0] == '#' && line[1] is ' ' or '\t')
            {
                var heading = line[2..].Trim().TrimEnd('#').TrimEnd();
                if (NoteTitle.TryNormalize(heading.ToString(), out var title))
                {
                    return title;
                }
            }
        }

        return null;
    }

    private static bool TryLoadMapping(string yaml, out YamlMappingNode mapping, out string? error)
    {
        mapping = new YamlMappingNode();
        error = null;
        if (ExceedsFlowNesting(yaml))
        {
            // Deeply nested flow collections recurse in the YAML loader; a crafted file must not crash the app.
            error = "The frontmatter nests collections too deeply.";
            return false;
        }

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
            {
                return true; // Empty frontmatter is valid and carries no metadata.
            }

            if (stream.Documents[0].RootNode is YamlMappingNode root)
            {
                mapping = root;
                return true;
            }

            error = "The frontmatter must be a YAML mapping (key: value pairs).";
            return false;
        }
        catch (YamlException exception)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"Invalid YAML at line {exception.Start.Line}, column {exception.Start.Column}: {exception.Message}");
            return false;
        }
        catch (ArgumentException exception)
        {
            // The representation model reports duplicate keys with ArgumentException.
            error = $"Invalid YAML: {exception.Message}";
            return false;
        }
    }

    private static bool ExceedsFlowNesting(ReadOnlySpan<char> yaml)
    {
        var depth = 0;
        foreach (var c in yaml)
        {
            if (c is '[' or '{')
            {
                if (++depth > MaxFlowNesting)
                {
                    return true;
                }
            }
            else if (c is ']' or '}' && depth > 0)
            {
                depth--;
            }
        }

        return false;
    }

    private static NoteMetadata ReadMetadata(YamlMappingNode mapping, string body, string fallbackTitle)
    {
        NoteId? id = null;
        string? title = null;
        string? project = null;
        string? type = null;
        DateOnly? created = null;
        DateOnly? updated = null;
        IReadOnlyList<Tag> tags = [];
        var links = NoteLinks.Empty;

        foreach (var (keyNode, value) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
            {
                continue;
            }

            switch (key.Trim().ToLowerInvariant())
            {
                case "id":
                    if (NoteId.TryParse(ReadScalar(value), out var parsedId))
                    {
                        id = parsedId;
                    }

                    break;
                case "title":
                    title = ReadScalar(value);
                    break;
                case "project":
                    project = NoteTitle.TryNormalize(ReadScalar(value), out var normalizedProject) ? normalizedProject : null;
                    break;
                case "type":
                    type = ReadScalar(value);
                    break;
                case "created":
                    created = ReadDate(value);
                    break;
                case "updated":
                    updated = ReadDate(value);
                    break;
                case "tags":
                    tags = Tag.NormalizeMany(ReadList(value, splitCommas: true));
                    break;
                case "links":
                    links = ReadLinks(value);
                    break;
                default:
                    break; // Unknown keys belong to the user and are preserved in the file.
            }
        }

        return new NoteMetadata
        {
            Id = id,
            Title = ResolveTitle(title, body, fallbackTitle),
            Project = project,
            Tags = tags,
            Type = NoteTypes.ParseOrDefault(type),
            Created = created,
            Updated = updated,
            Links = links,
        };
    }

    private static string ResolveTitle(string? frontmatterTitle, string body, string fallbackTitle)
    {
        if (NoteTitle.TryNormalize(frontmatterTitle, out var title))
        {
            return title;
        }

        return FindFirstHeading(body) ?? fallbackTitle;
    }

    private static NoteLinks ReadLinks(YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            return NoteLinks.Empty;
        }

        List<string> commits = [];
        List<string> tickets = [];
        List<string> notes = [];

        foreach (var (keyNode, value) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
            {
                continue;
            }

            switch (key.Trim().ToLowerInvariant())
            {
                case "commits":
                    commits = Distinct(ReadList(value, splitCommas: true));
                    break;
                case "tickets":
                    tickets = Distinct(ReadList(value, splitCommas: true));
                    break;
                case "notes":
                    notes = Distinct(ReadList(value, splitCommas: false).Select(NoteLinks.NormalizeNoteTarget));
                    break;
                default:
                    break;
            }
        }

        return commits.Count + tickets.Count + notes.Count == 0 ? NoteLinks.Empty : new NoteLinks(commits, tickets, notes);
    }

    private static List<string> Distinct(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (trimmed.Length is > 0 and <= 200 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>Reads a sequence of scalars; a single scalar is accepted as a one-item (or comma separated) list.</summary>
    private static List<string> ReadList(YamlNode node, bool splitCommas)
    {
        var items = new List<string>();
        switch (node)
        {
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    if (items.Count == MaxListItems)
                    {
                        break;
                    }

                    if (ReadScalar(child) is { Length: > 0 } item)
                    {
                        items.Add(item);
                    }
                }

                break;
            case YamlScalarNode when ReadScalar(node) is { Length: > 0 } scalar:
                if (splitCommas)
                {
                    items.AddRange(scalar.Split(',', MaxListItems, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else
                {
                    items.Add(scalar);
                }

                break;
            default:
                break;
        }

        return items;
    }

    private static string? ReadScalar(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Value is null)
        {
            return null;
        }

        // Plain `null`, `~` and empty values mean "no value" in YAML.
        if (scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        return scalar.Value;
    }

    private static DateOnly? ReadDate(YamlNode node)
    {
        var text = ReadScalar(node)?.Trim();
        if (text is null || text.Length < 10)
        {
            return null;
        }

        // Accepts `2026-09-12` and ISO timestamps such as `2026-09-12T10:30:00Z`.
        return DateOnly.TryParseExact(text.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
