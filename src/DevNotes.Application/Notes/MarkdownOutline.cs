using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DevNotes.Application.Notes;

/// <param name="Level">Heading level, 1 to 6.</param>
/// <param name="Text">Plain text of the heading.</param>
/// <param name="Line">Zero-based line of the heading inside the parsed text.</param>
public readonly record struct OutlineHeading(int Level, string Text, int Line);

/// <summary>Extracts the table of contents of a Markdown body.</summary>
public static class MarkdownOutline
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<OutlineHeading> Extract(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdown.Parse(markdown, _pipeline);
        var headings = new List<OutlineHeading>();
        var countedUpTo = 0;
        var line = 0;
        foreach (var block in document)
        {
            // Only top-level headings: headings nested in quotes or lists are not part of the outline.
            if (block is not HeadingBlock heading)
            {
                continue;
            }

            var text = GetPlainText(heading.Inline);
            if (text.Length == 0)
            {
                continue;
            }

            // The line is derived from the start offset: for setext headings Markdig reports the
            // line of the underline, while navigation wants the line where the text begins.
            var start = Math.Clamp(heading.Span.Start, countedUpTo, markdown.Length);
            line += markdown.AsSpan(countedUpTo, start - countedUpTo).Count('\n');
            countedUpTo = start;
            headings.Add(new OutlineHeading(heading.Level, text, line));
        }

        return headings;
    }

    private static string GetPlainText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        Append(builder, container);
        return builder.ToString().Trim();
    }

    private static void Append(StringBuilder builder, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    builder.Append(code.ContentSpan);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline nested:
                    Append(builder, nested);
                    break;
                default:
                    break;
            }
        }
    }
}
