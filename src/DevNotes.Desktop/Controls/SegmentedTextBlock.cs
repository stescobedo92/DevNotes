using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DevNotes.Application.Search;

namespace DevNotes.Desktop.Controls;

/// <summary>
/// Text block that renders search fragments: plain segments as-is and matched segments with the
/// highlight brush. Segments are data (text + flag), never markup, so note content cannot inject
/// formatting.
/// </summary>
public sealed class SegmentedTextBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<SnippetSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedTextBlock, IReadOnlyList<SnippetSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<SegmentedTextBlock, IBrush?>(nameof(HighlightBrush));

    public IReadOnlyList<SnippetSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    // Styled exactly like a TextBlock (classes such as "secondary" or "small" apply).
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty || change.Property == HighlightBrushProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        if (Segments is not { } segments)
        {
            return;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.Background = HighlightBrush;
                run.FontWeight = FontWeight.SemiBold;
            }

            inlines.Add(run);
        }
    }
}
