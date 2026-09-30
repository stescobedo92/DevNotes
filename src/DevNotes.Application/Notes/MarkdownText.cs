using System.Text;
using Markdig.Syntax.Inlines;

namespace DevNotes.Application.Notes;

/// <summary>Plain text of Markdown inline content (the words of a heading, the label of a link).</summary>
public static class MarkdownText
{
    /// <summary>
    /// Concatenates the literal text under <paramref name="container"/> in document order. The tree is
    /// walked with an explicit stack: a line such as "[[[[[[…" parses into thousands of nested
    /// inlines, which a recursive walk would turn into a stack overflow.
    /// </summary>
    public static string Of(ContainerInline container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var builder = new StringBuilder();
        var pending = new Stack<Inline>();
        pending.Push(container);
        while (pending.TryPop(out var inline))
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
                    // Children are pushed last-to-first so they are popped in document order.
                    for (var child = nested.LastChild; child is not null; child = child.PreviousSibling)
                    {
                        pending.Push(child);
                    }

                    break;
                default:
                    break;
            }
        }

        return builder.ToString();
    }
}
