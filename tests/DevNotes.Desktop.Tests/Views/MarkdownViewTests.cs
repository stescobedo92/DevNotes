using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Search;
using DevNotes.Desktop.Controls;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;

namespace DevNotes.Desktop.Tests.Views;

public sealed class MarkdownViewTests
{
    private readonly List<string?> _links = [];
    private readonly List<string?> _copied = [];

    [AvaloniaFact]
    public void Renders_HeadingsParagraphsAndInlineFormatting()
    {
        var view = Show("# Título\n\nTexto con **negrita**, *cursiva*, ~~tachado~~ y `código`.\n\n###### Nivel seis\n");

        var blocks = view.FindDescendants<SelectableTextBlock>().ToList();
        blocks.Should().HaveCount(3);
        blocks[0].Classes.Should().Contain(["md", "h1"]);
        TextOf(blocks[0]).Should().Be("Título");
        blocks[2].Classes.Should().Contain("h4", "levels above four share the smallest heading style");

        var inlines = blocks[1].Inlines!;
        TextOf(blocks[1]).Should().Be("Texto con negrita, cursiva, tachado y código.");
        inlines.OfType<Span>().Should().Contain(span => span.FontWeight == FontWeight.Bold);
        inlines.OfType<Span>().Should().Contain(span => span.FontStyle == FontStyle.Italic);
        inlines.OfType<Span>().Should().Contain(span => span.TextDecorations == TextDecorations.Strikethrough);
        inlines.OfType<Run>().Should().Contain(run => run.Text == "código" && run.Classes.Contains("mdInlineCode"));
    }

    [AvaloniaFact]
    public void Renders_ListsQuotesRulesAndTables()
    {
        var view = Show("""
            1. uno
            2. dos

            - [x] hecho
            - [ ] pendiente

            > cita

            ---

            | A | B |
            |---|--:|
            | 1 | 2 |
            """);

        var bullets = view.FindDescendants<TextBlock>().Where(block => block.Classes.Contains("mdBullet")).Select(block => block.Text).ToList();
        bullets.Should().Equal("1.", "2.", "•", "•");
        AllText(view).Should().Contain("☑ hecho").And.Contain("☐ pendiente").And.Contain("cita");
        view.FindDescendants<Border>().Should().Contain(border => border.Classes.Contains("mdQuote"));
        view.FindDescendants<Border>().Should().Contain(border => border.Classes.Contains("mdRule"));

        var cells = view.FindDescendants<Border>().Where(border => border.Classes.Contains("mdCell")).ToList();
        cells.Should().HaveCount(4);
        cells.Count(cell => cell.Classes.Contains("header")).Should().Be(2);
        ((SelectableTextBlock)cells[3].Child!).TextAlignment.Should().Be(TextAlignment.Right);
    }

    [AvaloniaFact]
    public void RawHtmlAndScripts_AreShownAsInertText()
    {
        var view = Show("Hola <script>alert('xss')</script> <img src=x onerror=alert(1)>\n\n<div onclick=\"evil()\">bloque</div>\n");

        var text = AllText(view);
        text.Should().Contain("<script>alert('xss')</script>").And.Contain("<div onclick=\"evil()\">bloque</div>");
        view.FindDescendants<Button>().Should().BeEmpty("HTML never becomes interactive content");
        view.FindDescendants<Image>().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Images_AreNeverLoaded()
    {
        var view = Show("![diagrama de red](https://tracker.example.com/pixel.png)\n");

        view.FindDescendants<Image>().Should().BeEmpty("a remote image would be a network request the user did not ask for");
        AllText(view).Should().Contain("diagrama de red").And.NotContain("tracker.example.com");
        _links.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Links_AreHandedToTheLinkCommand_WithTheirUrl()
    {
        var view = Show("Ver [la documentación](https://learn.microsoft.com/sql), <https://example.com> y [malo](file:///C:/Windows/System32/calc.exe).\n");

        var buttons = view.FindDescendants<Button>().Where(button => button.Classes.Contains("link")).ToList();
        buttons.Should().HaveCount(3);
        foreach (var button in buttons)
        {
            button.Command!.Execute(button.CommandParameter);
        }

        // The control never opens anything itself: every URL goes through the command, where the link policy applies.
        _links.Should().Equal("https://learn.microsoft.com/sql", "https://example.com", "file:///C:/Windows/System32/calc.exe");
        LinkPolicy.TryGetSafeUri(_links[2], out _).Should().BeFalse();
        ((TextBlock)buttons[0].Content!).Text.Should().Be("la documentación");
        ToolTip.GetTip(buttons[0]).Should().Be("https://learn.microsoft.com/sql");
    }

    [AvaloniaFact]
    public void CodeBlock_IsHighlighted_AndItsCopyButtonCopiesTheExactCode()
    {
        const string code = "SELECT Sku, Quantity\nFROM dbo.Inventory\nWHERE Quantity < 10;";
        var view = Show($"```sql\n{code}\n```\n");

        var block = view.FindDescendants<Border>().Single(border => border.Classes.Contains("mdCode"));
        var text = block.FindDescendants<SelectableTextBlock>().Single();
        TextOf(text).Should().Be(code);
        text.Inlines!.OfType<Run>().Select(run => run.Foreground).Where(brush => brush is not null).Distinct().Should().HaveCountGreaterThan(1, "SQL keywords and identifiers get different colours");
        block.FindDescendants<TextBlock>().Should().Contain(label => label.Text == "sql");

        var copy = block.FindDescendants<Button>().Single();
        copy.Command!.Execute(copy.CommandParameter);

        _copied.Should().Equal(code);
    }

    [AvaloniaFact]
    public void CodeBlock_UnknownLanguageOrIndented_IsPlainButStillCopyable()
    {
        var view = Show("```klingon\nnuqneH\n```\n\n    indented code\n");

        var blocks = view.FindDescendants<Border>().Where(border => border.Classes.Contains("mdCode")).ToList();
        blocks.Should().HaveCount(2);
        foreach (var copy in blocks.SelectMany(block => block.FindDescendants<Button>()))
        {
            copy.Command!.Execute(copy.CommandParameter);
        }

        _copied.Should().Equal("nuqneH", "indented code");
    }

    [AvaloniaFact]
    public void ThemeChange_RecoloursCodeBlocks()
    {
        var view = Show("```csharp\npublic class A { }\n```\n");
        var dark = CodeForegrounds(view);

        view.IsDark = false;
        Dispatcher.UIThread.RunJobs();

        CodeForegrounds(view).Should().NotEqual(dark);
    }

    [AvaloniaFact]
    public void EditingOneBlock_ReusesTheControlsOfUnchangedBlocks()
    {
        var view = Show("# Título\n\nPrimer párrafo.\n\n```sql\nSELECT 1;\n```\n");
        var before = ((StackPanel)view.Child!).Children.ToList();

        view.Markdown = "# Título\n\nPrimer párrafo editado.\n\n```sql\nSELECT 1;\n```\n";
        Dispatcher.UIThread.RunJobs();
        var after = ((StackPanel)view.Child!).Children.ToList();

        after.Should().HaveCount(3);
        after[0].Should().BeSameAs(before[0], "the heading did not change");
        after[1].Should().NotBeSameAs(before[1], "the edited paragraph is rebuilt");
        after[2].Should().BeSameAs(before[2], "the highlighted code block is reused instead of being tokenized again");
    }

    [AvaloniaTheory]
    [InlineData(10)]
    [InlineData(60)]
    public void DeeplyNestedQuotes_AreRenderedUpToALimit_ThenShownAsText(int levels)
    {
        var markdown = string.Concat(Enumerable.Repeat("> ", levels)) + "fondo de la cita\n";

        var view = Show(markdown);

        AllText(view).Should().Contain("fondo de la cita", "nothing the user wrote disappears");
        view.FindDescendants<Border>().Count(border => border.Classes.Contains("mdQuote"))
            .Should().BeLessThanOrEqualTo(MarkdownView.MaxBlockDepth, "each level is a nested control that layout has to walk");
    }

    [AvaloniaTheory]
    [InlineData("quotes")]
    [InlineData("lists")]
    [InlineData("brackets")]
    [InlineData("emphasis")]
    public void PathologicalNesting_NeverBreaksThePreview(string kind)
    {
        // Thousands of levels fit in a few kilobytes. Markdig refuses some of these inputs and parses
        // others into a tree thousands of nodes deep; either way the note must open.
        var markdown = kind switch
        {
            "quotes" => string.Concat(Enumerable.Repeat(">", 5_000)) + " texto final\n",
            "lists" => string.Concat(Enumerable.Repeat("- ", 3_000)) + "texto final\n",
            "brackets" => string.Concat(Enumerable.Repeat("[", 3_000)) + "texto final\n",
            _ => string.Concat(Enumerable.Repeat("*a ", 3_000)) + "texto final" + string.Concat(Enumerable.Repeat("*", 3_000)) + "\n",
        };

        var view = Show("# Antes\n\n" + markdown);

        AllText(view).Should().Contain("texto final");
    }

    [AvaloniaFact]
    public void WhileInactive_NothingIsRendered_AndItCatchesUpWhenShownAgain()
    {
        var view = Show("# Visible\n");
        var highlighter = new CountingHighlighter();
        view.Highlighter = highlighter;
        highlighter.Calls = 0;

        // Editor-only mode: the preview is hidden, typing must not pay for parsing and highlighting.
        view.IsActive = false;
        view.Markdown = "# Oculto\n\n```csharp\nvar x = 1;\n```\n";
        view.Markdown = "# Oculto\n\n```csharp\nvar x = 2;\n```\n";

        highlighter.Calls.Should().Be(0);
        AllText(view).Should().Contain("Visible");

        view.IsActive = true;
        Dispatcher.UIThread.RunJobs();

        highlighter.Calls.Should().Be(1, "only the latest text is rendered");
        AllText(view).Should().Contain("Oculto").And.Contain("var x = 2;");
    }

    [AvaloniaFact]
    public void ManyCodeBlocks_ShareOneHighlightingBudget_AndTheRestIsPlainText()
    {
        var view = Show("# Sin código\n");
        var highlighter = new CountingHighlighter();
        view.Highlighter = highlighter;
        highlighter.Calls = 0;
        view.HighlightBudget = TimeSpan.Zero; // Spent as soon as the first block has been highlighted.

        view.Markdown = string.Concat(Enumerable.Range(1, 5).Select(i => $"```csharp\nvar bloque{i} = {i};\n```\n\n"));
        Dispatcher.UIThread.RunJobs();

        highlighter.Calls.Should().Be(1, "the UI thread is not held for every block of a huge note");
        AllText(view).Should().Contain("var bloque1 = 1;").And.Contain("var bloque5 = 5;", "blocks that are not highlighted keep their text");
    }

    [AvaloniaFact]
    public void EmptyOrNullMarkdown_RendersNothing()
    {
        var view = Show(string.Empty);
        ((StackPanel)view.Child!).Children.Should().BeEmpty();

        view.Markdown = "texto";
        view.Markdown = null;

        ((StackPanel)view.Child!).Children.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void SegmentedTextBlock_RendersMatchesAsHighlightedRuns()
    {
        var block = new SegmentedTextBlock
        {
            HighlightBrush = Brushes.Gold,
            Segments = [new SnippetSegment("fix the ", false), new SnippetSegment("deadlock", true), new SnippetSegment(" now", false)],
        };

        var runs = block.Inlines!.OfType<Run>().ToList();
        runs.Select(run => run.Text).Should().Equal("fix the ", "deadlock", " now");
        runs[1].Background.Should().Be(Brushes.Gold);
        runs[1].FontWeight.Should().Be(FontWeight.SemiBold);
        runs[0].Background.Should().BeNull();

        block.Segments = [new SnippetSegment("<b>not markup</b>", false)];
        block.Inlines!.OfType<Run>().Single().Text.Should().Be("<b>not markup</b>");

        block.Segments = null;
        block.Inlines.Should().BeEmpty();
    }

    private sealed class CountingHighlighter : ICodeHighlighter
    {
        public int Calls { get; set; }

        public IReadOnlyList<IReadOnlyList<CodeToken>> Highlight(string code, string? language, bool dark)
        {
            Calls++;
            return [.. code.Split('\n').Select(line => (IReadOnlyList<CodeToken>)[new CodeToken(line, null, false, false)])];
        }

        public string? ResolveScope(string? language) => null;
    }

    private MarkdownView Show(string markdown)
    {
        var view = new MarkdownView
        {
            Highlighter = TextMateCodeHighlighter.WithoutTimeLimits(),
            LinkCommand = new RelayCommand<string?>(_links.Add),
            CopyCodeCommand = new RelayCommand<string?>(_copied.Add),
            Markdown = markdown,
        };
        var window = new Window { Content = new ScrollViewer { Content = view }, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static string TextOf(TextBlock block) =>
        block.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.Select(TextOf)) : block.Text ?? string.Empty;

    private static string TextOf(Inline inline) => inline switch
    {
        Run run => run.Text ?? string.Empty,
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(TextOf)),
        InlineUIContainer { Child: Button { Content: TextBlock text } } => text.Text ?? string.Empty,
        _ => string.Empty,
    };

    private static string AllText(MarkdownView view) =>
        string.Join('\n', view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(TextOf));

    private static List<IBrush?> CodeForegrounds(MarkdownView view) =>
        [.. view.FindDescendants<Border>().Single(border => border.Classes.Contains("mdCode"))
            .FindDescendants<SelectableTextBlock>().Single().Inlines!.OfType<Run>().Select(run => run.Foreground)];
}
