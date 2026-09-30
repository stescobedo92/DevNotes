using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DevNotes.Application.Notes;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;

namespace DevNotes.Desktop.Controls;

/// <summary>
/// Read-only Markdown preview rendered with native Avalonia controls from the Markdig syntax tree.
/// <para>
/// There is no HTML and no web view involved: raw HTML in a note is shown as plain text (the
/// pipeline disables HTML parsing), nothing is ever executed and nothing is fetched from the
/// network (images are represented by their alternative text). Links are only handed to
/// <see cref="LinkCommand"/>, which applies the link policy of the app.
/// </para>
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<ICodeHighlighter?> HighlighterProperty =
        AvaloniaProperty.Register<MarkdownView, ICodeHighlighter?>(nameof(Highlighter));

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsDark), defaultValue: true);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsActive), defaultValue: true);

    public static readonly StyledProperty<ICommand?> LinkCommandProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(LinkCommand));

    public static readonly StyledProperty<ICommand?> CopyCodeCommandProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(CopyCodeCommand));

    private const int MaxHeadingClass = 4;

    /// <summary>Containers (quotes, lists) nested deeper than this are shown as their source text.</summary>
    internal const int MaxBlockDepth = 24;

    /// <summary>Inlines nested deeper than this are flattened to their text.</summary>
    internal const int MaxInlineDepth = 48;

    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    private readonly StackPanel _panel = new();
    private readonly Dictionary<uint, IBrush> _brushes = [];

    // Controls of the previous render keyed by the source text of their block: typing in one
    // paragraph re-creates only that paragraph, every other block (including highlighted code) is reused.
    private Dictionary<string, Queue<Control>> _cache = new(StringComparer.Ordinal);
    private readonly Stopwatch _highlighting = new();
    private string _source = string.Empty;
    private bool _renderPending;

    public MarkdownView()
    {
        Child = _panel;
    }

    /// <summary>
    /// False while the preview is not on screen (editor-only mode). Nothing is parsed nor highlighted
    /// in that state; the pending content is rendered as soon as the preview is shown again.
    /// </summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ICodeHighlighter? Highlighter
    {
        get => GetValue(HighlighterProperty);
        set => SetValue(HighlighterProperty, value);
    }

    /// <summary>Selects the syntax-highlighting theme for code blocks.</summary>
    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    /// <summary>
    /// Time one render may spend highlighting code. The highlighter bounds each block, but a note
    /// can have dozens of them and the render runs on the UI thread: once the budget is spent the
    /// remaining blocks are shown as plain text.
    /// </summary>
    internal TimeSpan HighlightBudget { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Receives the URL of a clicked link as its parameter.</summary>
    public ICommand? LinkCommand
    {
        get => GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    /// <summary>Receives the text of a code block as its parameter.</summary>
    public ICommand? CopyCodeCommand
    {
        get => GetValue(CopyCodeCommandProperty);
        set => SetValue(CopyCodeCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            RequestRender();
        }
        else if (change.Property == IsActiveProperty)
        {
            if (_renderPending && IsActive)
            {
                Render();
            }
        }
        else if (change.Property == HighlighterProperty
            || change.Property == IsDarkProperty
            || change.Property == LinkCommandProperty
            || change.Property == CopyCodeCommandProperty)
        {
            // These are baked into the generated controls, so nothing can be reused.
            _cache.Clear();
            RequestRender();
        }
    }

    private void RequestRender()
    {
        if (IsActive)
        {
            Render();
        }
        else
        {
            _renderPending = true;
        }
    }

    private void Render()
    {
        _renderPending = false;
        _highlighting.Reset();
        var text = Markdown ?? string.Empty;
        _source = text;

        MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(text, _pipeline);
        }
        catch (ArgumentException)
        {
            // Markdig refuses input nested beyond its depth limit (hundreds of "> > > …" or a gigantic
            // table). The note is still shown, as the plain text it is.
            _cache.Clear();
            _panel.Children.Clear();
            _panel.Children.Add(CreatePlainText(text));
            return;
        }

        var next = new Dictionary<string, Queue<Control>>(StringComparer.Ordinal);

        _panel.Children.Clear();
        foreach (var block in document)
        {
            var key = GetSource(text, block);
            var control = key is not null && _cache.TryGetValue(key, out var reusable) && reusable.Count > 0
                ? reusable.Dequeue()
                : RenderBlock(block, tight: false, depth: 1);
            if (control is null)
            {
                continue;
            }

            _panel.Children.Add(control);
            if (key is not null)
            {
                if (!next.TryGetValue(key, out var queue))
                {
                    next[key] = queue = new Queue<Control>();
                }

                queue.Enqueue(control);
            }
        }

        _cache = next;
    }

    private static string? GetSource(string text, Block block)
    {
        var span = block.Span;
        return span.Start >= 0 && span.End >= span.Start && span.End < text.Length
            ? text.Substring(span.Start, span.Length)
            : null;
    }

    /// <param name="block">Block to render.</param>
    /// <param name="tight">Whether the block belongs to a tight list (no spacing between items).</param>
    /// <param name="depth">
    /// Nesting level of the block. Each level is a recursive call here and a nested control for the
    /// layout pass, so a note must not be able to choose it freely: past the limit the block is
    /// shown as its source text.
    /// </param>
    private Control? RenderBlock(Block block, bool tight, int depth)
    {
        if (depth > MaxBlockDepth && block is ContainerBlock)
        {
            return CreatePlainText(GetSource(_source, block) ?? string.Empty);
        }

        return block switch
        {
            HeadingBlock heading => CreateText(heading.Inline, "md", "h" + Math.Min(heading.Level, MaxHeadingClass).ToString(CultureInfo.InvariantCulture)),
            ParagraphBlock paragraph => tight ? CreateText(paragraph.Inline, "md", "listItem") : CreateText(paragraph.Inline, "md"),
            FencedCodeBlock fenced => CreateCodeBlock(fenced, fenced.Info),
            CodeBlock code => CreateCodeBlock(code, null),
            QuoteBlock quote => new Border { Classes = { "mdQuote" }, Child = CreateContainer(quote, tight: false, depth) },
            ListBlock list => CreateList(list, depth),
            ThematicBreakBlock => new Border { Classes = { "mdRule" } },
            Table table => CreateTable(table),
            LinkReferenceDefinitionGroup => null,
            ContainerBlock container => CreateContainer(container, tight, depth),
            LeafBlock leaf when leaf.Inline is not null => CreateText(leaf.Inline, "md"),
            LeafBlock leaf => CreatePlainText(leaf.Lines.ToString()),
            _ => null,
        };
    }

    private StackPanel CreateContainer(ContainerBlock container, bool tight, int depth)
    {
        var panel = new StackPanel();
        foreach (var child in container)
        {
            if (RenderBlock(child, tight, depth + 1) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private SelectableTextBlock CreateText(ContainerInline? inline, params string[] classes)
    {
        var block = new SelectableTextBlock();
        block.Classes.AddRange(classes);
        var inlines = block.Inlines ??= [];
        if (inline is not null)
        {
            AddInlines(inlines, inline, depth: 1);
        }

        return block;
    }

    private static SelectableTextBlock CreatePlainText(string text) =>
        new() { Classes = { "md" }, Text = text };

    private void AddInlines(InlineCollection target, ContainerInline container, int depth)
    {
        if (depth > MaxInlineDepth)
        {
            // "[[[[[[…" parses into thousands of nested inlines: past the limit the rest is plain text.
            target.Add(new Run(GetPlainText(container)));
            return;
        }

        foreach (var inline in container)
        {
            AddInline(target, inline, depth);
        }
    }

    private void AddInline(InlineCollection target, MarkdownInline inline, int depth)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;

            case CodeInline code:
                target.Add(new Run(code.Content) { Classes = { "mdInlineCode" } });
                break;

            case TaskList task:
                target.Add(new Run(task.Checked ? "☑" : "☐"));
                break;

            case AutolinkInline autolink:
                target.Add(CreateLink(autolink.Url, autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url));
                break;

            case LinkInline { IsImage: true } image:
                // Images are never loaded (a remote image would be a network request the user did not ask for).
                target.Add(new Run(ErrorMessages.Format(Strings.A11y_Image, GetPlainText(image))) { Classes = { "mdImage" } });
                break;

            case LinkInline link:
                target.Add(CreateLink(GetPlainText(link), link.Url));
                break;

            case EmphasisInline emphasis:
                var span = new Span();
                ApplyEmphasis(span, emphasis);
                AddInlines(span.Inlines, emphasis, depth + 1);
                target.Add(span);
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;

            case ContainerInline nested:
                AddInlines(target, nested, depth + 1);
                break;

            default:
                // Anything unknown (including raw HTML, should the pipeline ever produce it) is shown as text.
                target.Add(new Run(inline.ToString() ?? string.Empty));
                break;
        }
    }

    private static void ApplyEmphasis(Span span, EmphasisInline emphasis)
    {
        switch (emphasis.DelimiterChar)
        {
            case '~' when emphasis.DelimiterCount == 2:
                span.TextDecorations = TextDecorations.Strikethrough;
                break;
            case '*' or '_' when emphasis.DelimiterCount >= 2:
                span.FontWeight = FontWeight.Bold;
                break;
            case '*' or '_':
                span.FontStyle = FontStyle.Italic;
                break;
            default:
                break; // Other emphasis extras (sub/superscript, inserted, marked) keep the plain style.
        }
    }

    private InlineUIContainer CreateLink(string text, string? url)
    {
        var button = new Button
        {
            Classes = { "link" },
            Content = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline },
            Command = LinkCommand,
            CommandParameter = url,
        };
        ToolTip.SetTip(button, url);
        AutomationProperties.SetName(button, text);
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
    }

    private Border CreateCodeBlock(CodeBlock block, string? info)
    {
        var code = block.Lines.ToString();
        var language = TextMateCodeHighlighter.NormalizeLanguage(info);

        var text = new SelectableTextBlock { Classes = { "mdCodeText" } };
        var inlines = text.Inlines ??= [];
        var highlighter = Highlighter;
        if (highlighter is null || (_highlighting.ElapsedTicks > 0 && _highlighting.Elapsed >= HighlightBudget))
        {
            inlines.Add(new Run(code));
        }
        else
        {
            _highlighting.Start();
            IReadOnlyList<IReadOnlyList<CodeToken>> lines;
            try
            {
                lines = highlighter.Highlight(code, language, IsDark);
            }
            finally
            {
                _highlighting.Stop();
            }

            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    inlines.Add(new LineBreak());
                }

                foreach (var token in lines[i])
                {
                    var run = new Run(token.Text);
                    if (token.Foreground is { } color)
                    {
                        run.Foreground = GetBrush(color);
                    }

                    if (token.IsBold)
                    {
                        run.FontWeight = FontWeight.Bold;
                    }

                    if (token.IsItalic)
                    {
                        run.FontStyle = FontStyle.Italic;
                    }

                    inlines.Add(run);
                }
            }
        }

        var copy = new Button
        {
            Classes = { "ghost" },
            Content = new TextBlock { Text = Strings.Action_Copy, Classes = { "small" } },
            Command = CopyCodeCommand,
            CommandParameter = code,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(copy, Strings.A11y_CopyCode);

        var header = new Border
        {
            Classes = { "mdCodeHeader" },
            Child = new DockPanel
            {
                Children =
                {
                    WithDock(copy, Dock.Right),
                    new TextBlock { Text = language ?? string.Empty, Classes = { "secondary", "small" }, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
        };

        var container = new Border
        {
            Classes = { "mdCode" },
            Child = new DockPanel { Children = { WithDock(header, Dock.Top), scroller } },
        };
        AutomationProperties.SetName(container, Strings.A11y_CodeBlock);
        return container;
    }

    private Grid CreateList(ListBlock list, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Classes.Add("mdList");

        var number = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 1;
        var row = 0;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var bullet = new TextBlock
            {
                Classes = { "mdBullet" },
                Text = list.IsOrdered ? number.ToString(CultureInfo.CurrentCulture) + "." : "•",
            };
            Grid.SetRow(bullet, row);
            grid.Children.Add(bullet);

            var content = CreateContainer(listItem, tight: !list.IsLoose, depth + 1);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            number++;
            row++;
        }

        return grid;
    }

    private Border CreateTable(Table table)
    {
        var grid = new Grid();
        var columnCount = Math.Max(table.ColumnDefinitions.Count, 1);
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;
        foreach (var rowBlock in table)
        {
            if (rowBlock is not TableRow row)
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var columnIndex = 0;
            foreach (var cellBlock in row)
            {
                if (cellBlock is not TableCell cell || columnIndex >= columnCount)
                {
                    continue;
                }

                var text = cell.Count > 0 && cell[0] is LeafBlock leaf ? CreateText(leaf.Inline, "md", "cell") : CreateText(null, "md", "cell");
                text.TextAlignment = columnIndex < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[columnIndex].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    }
                    : TextAlignment.Left;

                var border = new Border { Classes = { "mdCell" }, Child = text };
                if (row.IsHeader)
                {
                    border.Classes.Add("header");
                }

                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, columnIndex);
                grid.Children.Add(border);
                columnIndex++;
            }

            rowIndex++;
        }

        return new Border
        {
            Classes = { "mdTable" },
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid,
            },
        };
    }

    private static string GetPlainText(ContainerInline container) => MarkdownText.Of(container);

    private IBrush GetBrush(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var brush))
        {
            brush = new ImmutableSolidColorBrush(argb);
            _brushes[argb] = brush;
        }

        return brush;
    }

    private static T WithDock<T>(T control, Dock dock)
        where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
