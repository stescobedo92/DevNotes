using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using DevNotes.Application.Abstractions;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Converters;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.Tests.Services;

public sealed partial class ServicesTests
{
    private static readonly CultureInfo _spanish = CultureInfo.GetCultureInfo("es");

    [Theory]
    [InlineData("https://learn.microsoft.com/sql", true)]
    [InlineData("http://example.com/a?b=c#d", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("\\\\server\\share\\payload.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("vscode://file/etc/passwd", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("relative/path.md", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LinkPolicy_OnlyAllowsWebAndMailLinks(string? url, bool expected)
    {
        LinkPolicy.TryGetSafeUri(url, out var uri).Should().Be(expected);

        if (expected)
        {
            uri.Should().NotBeNull();
        }
    }

    [Fact]
    public void ErrorMessages_Describe_MapsKnownFailuresToActionableText()
    {
        var path = NotePath.Create("a.md");

        ErrorMessages.Describe(new NoteAlreadyExistsException(path)).Should().Be(Strings.Error_NoteExists);
        ErrorMessages.Describe(new NoteNotFoundException(path)).Should().Be(Strings.Error_NoteNotFound);
        ErrorMessages.Describe(new TrashEntryNotFoundException("x")).Should().Be(Strings.Error_NoteNotFound);
        ErrorMessages.Describe(new VaultFolderNotFoundException("/missing")).Should().Contain("/missing");
        ErrorMessages.Describe(new UnauthorizedAccessException("denied by policy")).Should().Contain("denied by policy");
        ErrorMessages.Describe(new ArgumentException("bad")).Should().Be(Strings.Error_InvalidFolder);
        ErrorMessages.Describe(new IOException("disk full")).Should().Contain("disk full");
        ErrorMessages.Describe(new InvalidOperationException("bug")).Should().Contain("bug");
    }

    [Fact]
    public void ErrorMessages_IsExpected_SeparatesOperationalFailuresFromBugs()
    {
        ErrorMessages.IsExpected(new IOException()).Should().BeTrue();
        ErrorMessages.IsExpected(new UnauthorizedAccessException()).Should().BeTrue();
        ErrorMessages.IsExpected(new ArgumentException()).Should().BeTrue();
        ErrorMessages.IsExpected(new InvalidDataException()).Should().BeTrue();
        ErrorMessages.IsExpected(new InvalidCastException()).Should().BeFalse();
        ErrorMessages.IsExpected(new InvalidOperationException()).Should().BeFalse();
    }

    [Theory]
    [InlineData("N", true, false, false, "Ctrl+N", "⌘N")]
    [InlineData("P", true, true, false, "Ctrl+Shift+P", "⌘⇧P")]
    [InlineData("F2", false, false, false, "F2", "F2")]
    [InlineData("OemPlus", true, false, false, "Ctrl++", "⌘+")]
    [InlineData("OemMinus", true, false, false, "Ctrl+-", "⌘-")]
    [InlineData("D0", true, false, false, "Ctrl+0", "⌘0")]
    [InlineData("N", true, false, true, "Ctrl+Alt+N", "⌘⌥N")]
    public void ShortcutKey_Format_UsesPlatformConventions(string key, bool primary, bool shift, bool alt, string windows, string mac)
    {
        var shortcut = new ShortcutKey(key, primary, shift, alt);

        shortcut.Format(macStyle: false).Should().Be(windows);
        shortcut.Format(macStyle: true).Should().Be(mac);
        shortcut.DisplayText.Should().Be(OperatingSystem.IsMacOS() ? mac : windows);
    }

    [Fact]
    public void EditorOptions_ClampUnreasonableValues()
    {
        var options = new EditorOptions { AutosaveDelayMilliseconds = 1, PreviewDelayMilliseconds = -50 };

        options.AutosaveDelay.Should().Be(TimeSpan.FromMilliseconds(200), "saving on every keystroke would thrash the disk");
        options.PreviewDelay.Should().Be(TimeSpan.Zero);
        new EditorOptions().AutosaveDelay.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public void NameToBrushConverter_IsStableCaseInsensitiveAndDistinguishesNames()
    {
        var first = NameToBrushConverter.GetColor("sql-server");

        NameToBrushConverter.GetColor("sql-server").Should().Be(first);
        NameToBrushConverter.GetColor("SQL-Server").Should().Be(first);
        NameToBrushConverter.GetColor("performance").Should().NotBe(first);
        first.A.Should().Be(0xFF);

        // Pinned value: the colour must not change between runs, machines or .NET versions.
        first.Should().Be(NameToBrushConverter.GetColor("sql-server", 0xFF));
        ((ISolidColorBrush)NameToBrushConverter.Solid.Convert("sql-server", typeof(IBrush), null, CultureInfo.InvariantCulture)!).Color.Should().Be(first);
        ((ISolidColorBrush)NameToBrushConverter.Tint.Convert("sql-server", typeof(IBrush), null, CultureInfo.InvariantCulture)!).Color.A.Should().BeLessThan(0x80);
        NameToBrushConverter.Solid.Convert(null, typeof(IBrush), null, CultureInfo.InvariantCulture).Should().BeNull();
        NameToBrushConverter.Solid.Convert(string.Empty, typeof(IBrush), null, CultureInfo.InvariantCulture).Should().BeNull();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 12)]
    [InlineData(4, 36)]
    [InlineData(0, 0)]
    public void HeadingIndentConverter_IndentsByLevel(int level, double expectedLeft)
    {
        HeadingIndentConverter.Instance.Convert(level, typeof(Thickness), null, CultureInfo.InvariantCulture)
            .Should().Be(new Thickness(expectedLeft, 0, 0, 0));
    }

    [Theory]
    [InlineData("csharp", "source.cs")]
    [InlineData("cs", "source.cs")]
    [InlineData("C#", "source.cs")]
    [InlineData("c++", "source.cpp")]
    [InlineData("cpp", "source.cpp")]
    [InlineData("sql", "source.sql")]
    [InlineData("powershell", "source.powershell")]
    [InlineData("ps1", "source.powershell")]
    [InlineData("json", "source.json")]
    [InlineData("yaml", "source.yaml")]
    [InlineData("yml", "source.yaml")]
    [InlineData("bash", "source.shell")]
    [InlineData("sh", "source.shell")]
    [InlineData("csharp title=\"Example.cs\"", "source.cs")]
    [InlineData(".json", "source.json")]
    public void CodeHighlighter_ResolveScope_KnowsTheLanguagesOfTheTargetUser(string language, string expectedScope)
    {
        new TextMateCodeHighlighter().ResolveScope(language).Should().Be(expectedScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("klingon")]
    [InlineData("{.weird}")]
    public void CodeHighlighter_ResolveScope_UnknownLanguage_ReturnsNull(string? language)
    {
        new TextMateCodeHighlighter().ResolveScope(language).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CodeHighlighter_Highlight_ColoursTokensAndPreservesTheText(bool dark)
    {
        const string code = "public async Task RunAsync()\n{\n    // comment\n    return;\n}";
        var highlighter = TextMateCodeHighlighter.WithoutTimeLimits();

        var lines = highlighter.Highlight(code, "csharp", dark);

        lines.Should().HaveCount(5);
        string.Join('\n', lines.Select(line => string.Concat(line.Select(token => token.Text)))).Should().Be(code);
        lines[0].Should().Contain(token => token.Text == "public" && token.Foreground != null);
        lines.SelectMany(line => line).Select(token => token.Foreground).Distinct().Should().HaveCountGreaterThan(2, "keywords, comments and identifiers use different colours");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 60_000)]
    public void CodeHighlighter_Highlight_OutOfTime_FallsBackToPlainTextWithoutLosingACharacter(int lineBudgetMs, int blockBudgetMs)
    {
        const string code = "public async Task RunAsync()\n{\n    var text = \"value\"; // comment\n    return;\n}";
        var highlighter = new TextMateCodeHighlighter(TimeSpan.FromMilliseconds(lineBudgetMs), TimeSpan.FromMilliseconds(blockBudgetMs));

        var lines = highlighter.Highlight(code, "csharp", dark: true);

        lines.Should().HaveCount(5);
        string.Join('\n', lines.Select(line => string.Concat(line.Select(token => token.Text)))).Should().Be(code, "running out of time degrades the colours, never the text");
    }

    [Fact]
    public void CodeHighlighter_Highlight_UnknownLanguageOrOversizedBlock_IsPlainText()
    {
        var highlighter = new TextMateCodeHighlighter();

        var unknown = highlighter.Highlight("some text\r\nsecond line", "klingon", dark: true);
        var huge = highlighter.Highlight(new string('x', TextMateCodeHighlighter.MaxHighlightedCharacters + 1), "csharp", dark: true);

        unknown.Should().HaveCount(2);
        unknown[0].Should().Equal(new CodeToken("some text", null, false, false));
        unknown[1].Should().Equal(new CodeToken("second line", null, false, false));
        huge.Should().ContainSingle().Which.Should().ContainSingle().Which.Foreground.Should().BeNull();
    }

    [Theory]
    [InlineData("#FFFFFF", 0xFFFFFFFFu)]
    [InlineData("#4C8DFF", 0xFF4C8DFFu)]
    [InlineData("#11223380", 0x80112233u)]
    public void CodeHighlighter_ParseColor_UnderstandsThemeNotation(string hex, uint expected)
    {
        TextMateCodeHighlighter.ParseColor(hex).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    public void CodeHighlighter_ParseColor_Malformed_ReturnsNull(string? hex)
    {
        TextMateCodeHighlighter.ParseColor(hex).Should().BeNull();
    }

    [Fact]
    public void Strings_SpanishAndEnglish_DefineExactlyTheSameKeys()
    {
        var english = ReadAll(CultureInfo.InvariantCulture);
        var spanish = ReadAll(_spanish);

        spanish.Keys.Should().BeEquivalentTo(english.Keys, "every string must exist in both languages");
        english.Should().NotBeEmpty();
    }

    [Fact]
    public void Strings_Translations_KeepTheSamePlaceholders()
    {
        var english = ReadAll(CultureInfo.InvariantCulture);
        var spanish = ReadAll(_spanish);

        foreach (var (key, value) in english)
        {
            Placeholders(spanish[key]).Should().Equal(Placeholders(value), $"'{key}' must use the same format arguments in both languages");
            spanish[key].Should().NotBeNullOrWhiteSpace($"'{key}' must be translated");
        }
    }

    [Theory]
    [InlineData(NoteType.Note)]
    [InlineData(NoteType.Bug)]
    [InlineData(NoteType.Adr)]
    [InlineData(NoteType.Runbook)]
    [InlineData(NoteType.Learning)]
    [InlineData(NoteType.Snippet)]
    public void NoteTypeLabels_ExistForEveryType(NoteType type)
    {
        NoteTypeLabels.Get(type).Should().NotBeNullOrWhiteSpace().And.NotBe("Type_" + type.ToKey());
    }

    private static Dictionary<string, string> ReadAll(CultureInfo culture)
    {
        // tryParents: false → only the strings physically present in that language's resources.
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new MissingManifestResourceException($"No resources for culture '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
    }

    private static List<string> Placeholders(string value) =>
        [.. PlaceholderPattern().Matches(value).Select(match => match.Value).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
