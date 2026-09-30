using System.Globalization;
using System.Text;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

/// <summary>
/// Renders note templates. Placeholders are <c>{{id}}</c>, <c>{{title}}</c>, <c>{{project}}</c>,
/// <c>{{date}}</c>, <c>{{type}}</c> and <c>{{body}}</c> (case-insensitive, spaces inside the braces
/// allowed); anything else between double braces is left as it is. A frontmatter line whose value
/// is an empty placeholder (a note without project) is dropped instead of writing an empty key.
/// </summary>
public static class NoteTemplates
{
    /// <summary>Content of a brand-new note without a template: complete frontmatter plus a level-1 heading.</summary>
    public static string CreateDefault(NoteId id, string title, NoteType type, string? project, DateOnly today)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var context = new TemplateContext(id, title, project, today, type);
        var text = Render(BuiltInTemplates.Get("note", CultureInfo.InvariantCulture), context);
        return type == NoteType.Note ? text : FrontmatterEditor.SetScalars(text, [new FrontmatterAssignment("type", type.ToKey())]);
    }

    public static string Render(string template, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        var output = new StringBuilder(template.Length + (context.Body?.Length ?? 0) + 64);
        var bodyUsed = false;
        var inFrontmatter = false;
        var lineIndex = 0;

        // Every line is written with its own line feed, so the final line terminator of the template
        // (if any) must not count as one more, empty, line.
        var source = template.AsSpan();
        if (source.EndsWith('\n'))
        {
            source = source[..^1];
            if (source.EndsWith('\r'))
            {
                source = source[..^1];
            }
        }

        foreach (var rawLine in source.EnumerateLines())
        {
            var line = rawLine;
            if (lineIndex == 0 && line.Length > 0 && line[0] == '﻿')
            {
                line = line[1..];
            }

            if (line.SequenceEqual("---"))
            {
                // The block opens on the first line and closes on the next "---".
                inFrontmatter = lineIndex == 0;
            }

            var wasBodyUsed = bodyUsed;
            var rendered = Substitute(line, context, inFrontmatter, out var emptyPlaceholder, ref bodyUsed);
            lineIndex++;

            // "project: {{project}}" without a project would leave "project:" behind: the line goes away,
            // and so does the line of an empty {{body}}.
            if (emptyPlaceholder && ((inFrontmatter && IsBareKey(rendered)) || (bodyUsed != wasBodyUsed && rendered.AsSpan().IsWhiteSpace())))
            {
                continue;
            }

            output.Append(rendered).Append('\n');
        }

        if (!bodyUsed && !string.IsNullOrWhiteSpace(context.Body))
        {
            EnsureBlankLine(output);
            output.Append(context.Body.TrimEnd()).Append('\n');
        }

        return output.ToString();
    }

    private static string Substitute(ReadOnlySpan<char> line, TemplateContext context, bool yaml, out bool emptyPlaceholder, ref bool bodyUsed)
    {
        emptyPlaceholder = false;
        var open = line.IndexOf("{{", StringComparison.Ordinal);
        if (open < 0)
        {
            return line.ToString();
        }

        var result = new StringBuilder(line.Length + 32);
        while (open >= 0)
        {
            result.Append(line[..open]);
            line = line[(open + 2)..];
            var close = line.IndexOf("}}", StringComparison.Ordinal);
            if (close < 0)
            {
                result.Append("{{");
                break;
            }

            var name = line[..close].Trim();
            line = line[(close + 2)..];
            if (TryResolve(name, context, yaml, ref bodyUsed, out var value))
            {
                if (value.Length == 0)
                {
                    emptyPlaceholder = true;
                }

                result.Append(value);
            }
            else
            {
                // Unknown placeholder: the user may be writing about templates; leave it untouched.
                result.Append("{{").Append(name).Append("}}");
            }

            open = line.IndexOf("{{", StringComparison.Ordinal);
        }

        result.Append(line);
        return result.ToString();
    }

    /// <param name="name">Placeholder name.</param>
    /// <param name="context">Values to substitute.</param>
    /// <param name="yaml">Inside the frontmatter the values are formatted as YAML scalars; in the body they are plain text.</param>
    /// <param name="bodyUsed">Set when the body placeholder is met.</param>
    /// <param name="value">The substituted text.</param>
    private static bool TryResolve(ReadOnlySpan<char> name, TemplateContext context, bool yaml, ref bool bodyUsed, out string value)
    {
        if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            value = Scalar(context.Id.Value, yaml);
            return true;
        }

        if (name.Equals("title", StringComparison.OrdinalIgnoreCase))
        {
            value = Scalar(context.Title, yaml);
            return true;
        }

        if (name.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            value = string.IsNullOrWhiteSpace(context.Project) ? string.Empty : Scalar(context.Project.Trim(), yaml);
            return true;
        }

        if (name.Equals("date", StringComparison.OrdinalIgnoreCase))
        {
            value = YamlScalar.FormatDate(context.Date);
            return true;
        }

        if (name.Equals("type", StringComparison.OrdinalIgnoreCase))
        {
            value = context.Type.ToKey();
            return true;
        }

        if (name.Equals("body", StringComparison.OrdinalIgnoreCase))
        {
            bodyUsed = true;
            value = context.Body?.Trim() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Scalar(string value, bool yaml) => yaml ? YamlScalar.Format(value) : value;

    /// <summary>"project:" or "project: " with nothing after the colon.</summary>
    private static bool IsBareKey(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && line.AsSpan(colon + 1).IsWhiteSpace() && !line.AsSpan(0, colon).ContainsAny(' ', '\t');
    }

    private static void EnsureBlankLine(StringBuilder output)
    {
        if (output.Length == 0)
        {
            return;
        }

        if (output[^1] != '\n')
        {
            output.Append('\n');
        }

        if (output.Length < 2 || output[^2] != '\n')
        {
            output.Append('\n');
        }
    }
}
